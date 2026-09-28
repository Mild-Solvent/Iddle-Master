const REFRESH_MS = 2000;

const ramText = document.querySelector("#ramText");
const gameButton = document.querySelector("#gameButton");
const gameText = document.querySelector("#gameText");
const tabsTitle = document.querySelector("#tabsTitle");
const tabList = document.querySelector("#tabList");
const extTitle = document.querySelector("#extTitle");
const extList = document.querySelector("#extList");
const heapButton = document.querySelector("#heapButton");
const sleepOthersButton = document.querySelector("#sleepOthersButton");
const logBox = document.querySelector("#logBox");

const GAME_HINT_OFF =
  "Sleeps every background tab and parks extensions. The tab you are on keeps running.";

let gameIsOn = false;
let busy = false;
let lastLog = "";

function send(type, extra = {}) {
  return chrome.runtime.sendMessage({ type, ...extra });
}

function gb(bytes) {
  return (bytes / 1073741824).toFixed(1);
}

function mb(bytes) {
  return `${Math.round(bytes / 1048576)} MB`;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}

function button(className, text, onClick) {
  const node = element("button", className, text);
  node.type = "button";
  node.addEventListener("click", onClick);
  return node;
}

// One action at a time: every button waits for the last one to land, then
// the list is redrawn from what the browser says is true now.
async function act(type, extra) {
  if (busy) {
    return;
  }
  busy = true;
  let failure = "";
  try {
    const result = await send(type, extra);
    if (result && result.ok === false) {
      failure = result.message || "That did not work.";
    }
  } catch (error) {
    failure = error?.message || "Something went wrong.";
  }
  busy = false;
  await refresh();
  if (failure) {
    showError(failure);
  }
}

function showError(message) {
  gameText.textContent = message;
  gameText.classList.add("error");
}

function tabMeta(tab) {
  const parts = [];
  if (tab.heap) {
    parts.push(`heap ${mb(tab.heap)}`);
  }
  parts.push(tab.host);
  return parts.filter(Boolean).join("  ");
}

function renderTabs(tabs) {
  const asleep = tabs.filter((tab) => tab.discarded).length;
  tabsTitle.textContent = `TABS  ${tabs.length - asleep} awake / ${asleep} asleep`;

  const sorted = [...tabs].sort(
    (a, b) => a.discarded - b.discarded || (b.heap || 0) - (a.heap || 0)
  );

  tabList.replaceChildren(
    ...sorted.map((tab) => {
      const row = element("li", tab.discarded ? "asleep" : "");
      row.append(element("span", "dot"));

      const name = button("name", tab.title, () => act("focusTab", { tabId: tab.id }));
      name.title = tab.discarded ? "Open it (this wakes it)" : "Go to this tab";
      row.append(name);

      if (tab.active) {
        row.append(element("span", "meta tag", "on screen"));
      } else if (tab.audible) {
        row.append(element("span", "meta tag", "sound"));
      }
      row.append(element("span", "meta", tabMeta(tab)));

      row.append(
        tab.discarded
          ? button("act", "wake", () => act("wakeTab", { tabId: tab.id }))
          : button("act", "sleep", () => act("sleepTab", { tabId: tab.id }))
      );
      return row;
    })
  );
}

function renderExtensions(extensions) {
  const on = extensions.filter((item) => item.enabled).length;
  extTitle.textContent = `EXTENSIONS  ${on} on / ${extensions.length - on} off`;

  extList.replaceChildren(
    ...extensions.map((item) => {
      const row = element("li", item.enabled ? "" : "asleep");
      row.append(element("span", "dot"));
      row.append(element("span", "name", item.name));
      if (item.parked) {
        row.append(element("span", "meta tag", "parked"));
      }

      const keep = button(`act keep${item.keep ? " on" : ""}`, "keep", () =>
        act("setKeep", { id: item.id, keep: !item.keep })
      );
      keep.title = item.keep
        ? "Game mode leaves this one on. Click to let game mode park it."
        : "Click to make game mode leave this one on.";
      row.append(keep);

      const toggle = button("act", item.enabled ? "off" : "on", () =>
        act("setExtension", { id: item.id, enabled: !item.enabled })
      );
      toggle.title = item.enabled ? "Turn this extension off" : "Turn this extension on";
      toggle.disabled = item.enabled && !item.mayDisable;
      row.append(toggle);
      return row;
    })
  );
}

function renderLog(lines) {
  const text = lines.join("\n");
  if (text === lastLog) {
    return;
  }
  lastLog = text;
  logBox.textContent = text || "nothing yet";
  logBox.scrollTop = logBox.scrollHeight;
}

function render(state) {
  ramText.textContent =
    `RAM ${gb(state.memory.availableCapacity)} free of ${gb(state.memory.capacity)} GB`;

  gameIsOn = Boolean(state.game);
  gameButton.textContent = gameIsOn ? "GAME MODE IS ON  -  WAKE UP" : "GAME MODE";
  gameButton.classList.toggle("on", gameIsOn);
  gameText.classList.remove("error");
  gameText.textContent = gameIsOn
    ? `${state.game.parked.length} extension(s) parked. Waking up turns them back on; tabs wake when you open them.`
    : GAME_HINT_OFF;

  heapButton.hidden = state.heapGranted;
  renderTabs(state.tabs);
  renderExtensions(state.extensions);
  renderLog(state.log);
}

async function refresh() {
  if (busy) {
    return;
  }
  render(await send("snapshot"));
}

gameButton.addEventListener("click", () => {
  gameButton.textContent = "WORKING...";
  act(gameIsOn ? "gameOff" : "gameOn");
});

sleepOthersButton.addEventListener("click", () => act("sleepOthers"));

// Reading a tab's heap means running one line inside it, which the browser
// only allows once you have said yes to access on all sites.
heapButton.addEventListener("click", async () => {
  await chrome.permissions.request({ origins: ["<all_urls>"] });
  await refresh();
});

refresh().catch((error) => showError(error?.message || "Could not read the browser."));
setInterval(() => refresh().catch(() => {}), REFRESH_MS);
