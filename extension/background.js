const FROZEN_PATH = "/frozen.html";
const LEGACY_KEY = "frozenTabs";
const STATE_KEY = "idleTabs";
const LOG_MAX = 40;
const HEAP_TIMEOUT_MS = 800;
const SETTLE_MS = 2000;

// Extensions game mode leaves alone until told otherwise. Claude is here
// because other sessions drive the browser through it.
const KEEP_DEFAULT = ["fcoeoabgfenejglbffodgkkbkcdhcgfn"];

// ---------------------------------------------------------------------------
// state
// ---------------------------------------------------------------------------

async function loadState() {
  const stored = await chrome.storage.local.get(STATE_KEY);
  return { keep: [...KEEP_DEFAULT], game: null, log: [], ...(stored[STATE_KEY] || {}) };
}

// Every write goes through one queue, so two actions finishing together
// cannot overwrite each other's log lines.
let queue = Promise.resolve();

function mutate(change) {
  const run = queue.then(async () => {
    const state = await loadState();
    await change(state);
    await chrome.storage.local.set({ [STATE_KEY]: state });
    return state;
  });
  queue = run.catch(() => {});
  return run;
}

function log(text) {
  const stamp = new Date().toLocaleTimeString([], { hour12: false });
  return mutate((state) => {
    state.log.push(`${stamp}  ${text}`);
    state.log = state.log.slice(-LOG_MAX);
  });
}

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function gb(bytes) {
  return (bytes / 1073741824).toFixed(1);
}

function shortTitle(tab) {
  const title = tab.title || tab.url || "tab";
  return title.length > 40 ? `${title.slice(0, 39)}…` : title;
}

// ---------------------------------------------------------------------------
// tabs
// ---------------------------------------------------------------------------

// The browser refuses to discard the tab you are looking at, so step off it
// first. Stepping onto a sleeping tab would wake it, so those do not count.
async function stepOff(tab) {
  const others = await chrome.tabs.query({ windowId: tab.windowId });
  const awake = others
    .filter((other) => other.id !== tab.id && !other.discarded)
    .sort((a, b) => (b.lastAccessed || 0) - (a.lastAccessed || 0));
  if (awake.length) {
    await chrome.tabs.update(awake[0].id, { active: true });
  } else {
    await chrome.tabs.create({ windowId: tab.windowId, active: true });
  }
}

async function discard(tab) {
  if (tab.discarded) {
    return false;
  }
  try {
    if (tab.active) {
      await stepOff(tab);
    }
    const after = await chrome.tabs.discard(tab.id);
    return Boolean(after && after.discarded);
  } catch {
    return false;
  }
}

async function sleepTab(tabId) {
  const tab = await chrome.tabs.get(tabId);
  const slept = await discard(tab);
  await log(slept ? `sleep  ${shortTitle(tab)}` : `could not sleep  ${shortTitle(tab)}`);
  return { ok: slept };
}

async function wakeTab(tabId) {
  const tab = await chrome.tabs.get(tabId);
  await chrome.tabs.reload(tabId);
  await log(`wake   ${shortTitle(tab)}`);
  return { ok: true };
}

async function focusTab(tabId) {
  const tab = await chrome.tabs.update(tabId, { active: true });
  await chrome.windows.update(tab.windowId, { focused: true });
  return { ok: true };
}

// Background tabs only: the one on screen in each window stays, and so does
// anything playing sound.
async function sleepBackgroundTabs() {
  const tabs = await chrome.tabs.query({});
  let slept = 0;
  for (const tab of tabs) {
    if (tab.active || tab.discarded || tab.audible) {
      continue;
    }
    if (await discard(tab)) {
      slept += 1;
    }
  }
  return slept;
}

async function sleepOthers() {
  const slept = await sleepBackgroundTabs();
  await log(`slept ${slept} background tab${slept === 1 ? "" : "s"}`);
  return { ok: true, slept };
}

// ---------------------------------------------------------------------------
// extensions
// ---------------------------------------------------------------------------

async function listExtensions() {
  const all = await chrome.management.getAll();
  return all
    .filter((item) => item.type === "extension" && item.id !== chrome.runtime.id)
    .sort((a, b) => a.name.localeCompare(b.name));
}

async function setExtension(id, enabled) {
  const item = await chrome.management.get(id);
  await chrome.management.setEnabled(id, enabled);
  await mutate((state) => {
    // Switched by hand, so game mode no longer owns it.
    if (state.game) {
      state.game.parked = state.game.parked.filter((parked) => parked !== id);
    }
  });
  await log(`${enabled ? "on " : "off"}    ${item.name}`);
  return { ok: true };
}

async function setKeep(id, keep) {
  await mutate((state) => {
    state.keep = state.keep.filter((kept) => kept !== id);
    if (keep) {
      state.keep.push(id);
    }
  });
  return { ok: true };
}

// ---------------------------------------------------------------------------
// game mode
// ---------------------------------------------------------------------------

async function gameOn() {
  const before = await chrome.system.memory.getInfo();
  const { keep } = await loadState();

  const slept = await sleepBackgroundTabs();

  const parked = [];
  for (const item of await listExtensions()) {
    if (!item.enabled || !item.mayDisable || keep.includes(item.id)) {
      continue;
    }
    try {
      await chrome.management.setEnabled(item.id, false);
      parked.push(item.id);
    } catch {
      // policy-installed, or gone since the list was read
    }
  }

  await mutate((state) => {
    state.game = { at: Date.now(), parked };
  });

  // The memory comes back as the processes exit, not the moment they are told to.
  await wait(SETTLE_MS);
  const after = await chrome.system.memory.getInfo();
  await log(
    `GAME MODE ON  ${slept} tab${slept === 1 ? "" : "s"} asleep, ` +
      `${parked.length} extension${parked.length === 1 ? "" : "s"} parked, ` +
      `free RAM ${gb(before.availableCapacity)} -> ${gb(after.availableCapacity)} GB`
  );
  return { ok: true, slept, parked: parked.length };
}

async function gameOff() {
  const { game } = await loadState();
  let woken = 0;
  for (const id of game ? game.parked : []) {
    try {
      await chrome.management.setEnabled(id, true);
      woken += 1;
    } catch {
      // uninstalled while it was parked
    }
  }
  await mutate((state) => {
    state.game = null;
  });
  await log(
    `GAME MODE OFF  ${woken} extension${woken === 1 ? "" : "s"} back, tabs wake when you open them`
  );
  return { ok: true, woken };
}

async function toggleGame() {
  const { game } = await loadState();
  return game ? gameOff() : gameOn();
}

// ---------------------------------------------------------------------------
// snapshot for the popup
// ---------------------------------------------------------------------------

function hostOf(url) {
  try {
    const parsed = new URL(url);
    return parsed.host || parsed.protocol.replace(":", "");
  } catch {
    return "";
  }
}

function isWebUrl(url) {
  return /^https?:/.test(url || "");
}

// JS heap of the tab's renderer. It is the only per-tab number an extension
// can get on a release build, and it is a floor: images, DOM and GPU memory
// are not in it.
async function heapOf(tabId) {
  const read = chrome.scripting
    .executeScript({
      target: { tabId },
      func: () => (performance.memory ? performance.memory.usedJSHeapSize : null)
    })
    .then((results) => (results && results[0] ? results[0].result : null))
    .catch(() => null);
  return Promise.race([read, wait(HEAP_TIMEOUT_MS).then(() => null)]);
}

async function snapshot() {
  const [state, memory, tabs, extensions, heapGranted] = await Promise.all([
    loadState(),
    chrome.system.memory.getInfo(),
    chrome.tabs.query({}),
    listExtensions(),
    chrome.permissions.contains({ origins: ["<all_urls>"] })
  ]);

  const heaps = await Promise.all(
    tabs.map((tab) =>
      heapGranted && !tab.discarded && isWebUrl(tab.url) ? heapOf(tab.id) : null
    )
  );

  return {
    memory,
    heapGranted,
    game: state.game,
    log: state.log,
    tabs: tabs.map((tab, index) => ({
      id: tab.id,
      windowId: tab.windowId,
      title: tab.title || tab.url || "",
      host: hostOf(tab.url),
      active: tab.active,
      discarded: tab.discarded,
      audible: Boolean(tab.audible),
      pinned: tab.pinned,
      heap: heaps[index]
    })),
    extensions: extensions.map((item) => ({
      id: item.id,
      name: item.name,
      enabled: item.enabled,
      mayDisable: item.mayDisable,
      keep: state.keep.includes(item.id),
      parked: Boolean(state.game && state.game.parked.includes(item.id))
    }))
  };
}

// ---------------------------------------------------------------------------
// tabs frozen by 1.x are still sitting on the placeholder page
// ---------------------------------------------------------------------------

function getFrozenIdFromUrl(url) {
  try {
    const parsed = new URL(url);
    const frozenUrl = new URL(chrome.runtime.getURL(FROZEN_PATH));
    if (parsed.origin !== frozenUrl.origin || parsed.pathname !== frozenUrl.pathname) {
      return null;
    }
    return parsed.searchParams.get("id");
  } catch {
    return null;
  }
}

async function getFrozenTabs() {
  const stored = await chrome.storage.local.get(LEGACY_KEY);
  return stored[LEGACY_KEY] || {};
}

async function legacyStatus(sender) {
  const id = sender.tab && sender.tab.url ? getFrozenIdFromUrl(sender.tab.url) : null;
  if (!id) {
    return { status: "error", message: "Not a frozen tab." };
  }
  const frozenTabs = await getFrozenTabs();
  return { status: "frozen", id, frozen: frozenTabs[id] || null };
}

async function restoreFrozenId(id, sender) {
  const frozenTabs = await getFrozenTabs();
  const frozen = frozenTabs[id];
  if (!sender.tab || !frozen || !frozen.originalUrl) {
    return {
      ok: false,
      status: "missing",
      message: "The saved page for this frozen tab is missing."
    };
  }
  delete frozenTabs[id];
  await chrome.storage.local.set({ [LEGACY_KEY]: frozenTabs });
  await chrome.tabs.update(sender.tab.id, { url: frozen.originalUrl });
  return { ok: true, status: "restored", url: frozen.originalUrl };
}

// ---------------------------------------------------------------------------
// wiring
// ---------------------------------------------------------------------------

chrome.commands.onCommand.addListener(async (command) => {
  try {
    if (command === "sleep-tab") {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      if (tab) {
        await sleepTab(tab.id);
      }
    } else if (command === "game-mode") {
      await toggleGame();
    }
  } catch (error) {
    console.error(error);
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const handlers = {
    snapshot,
    sleepTab: () => sleepTab(message.tabId),
    wakeTab: () => wakeTab(message.tabId),
    focusTab: () => focusTab(message.tabId),
    sleepOthers,
    gameOn,
    gameOff,
    setExtension: () => setExtension(message.id, message.enabled),
    setKeep: () => setKeep(message.id, message.keep),
    getStatus: () => legacyStatus(sender),
    restoreFrozenId: () => restoreFrozenId(message.id, sender)
  };

  const handler = handlers[message?.type];
  if (!handler) {
    sendResponse({ ok: false, status: "error", message: "Unknown request." });
    return false;
  }

  handler()
    .then(sendResponse)
    .catch((error) => {
      sendResponse({
        ok: false,
        status: "error",
        message: error?.message || "Something went wrong."
      });
    });
  return true;
});
