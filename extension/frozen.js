const restoreButton = document.querySelector("#restoreButton");
const summary = document.querySelector("#summary");
const id = new URLSearchParams(location.search).get("id");

function send(type, extra = {}) {
  return chrome.runtime.sendMessage({ type, ...extra });
}

async function loadDetails() {
  if (!id) {
    restoreButton.disabled = true;
    summary.textContent = "This frozen tab is missing its restore information.";
    return;
  }

  const status = await send("getStatus");
  if (status.status === "frozen" && status.frozen) {
    const title = status.frozen.title || status.frozen.originalUrl;
    summary.textContent = `${title} has been unloaded to free memory.`;
  }
}

restoreButton.addEventListener("click", async () => {
  restoreButton.disabled = true;
  restoreButton.textContent = "Restoring...";
  const result = await send("restoreFrozenId", { id });
  if (!result.ok) {
    restoreButton.textContent = "Restore tab";
    restoreButton.disabled = false;
    summary.textContent = result.message || "Could not restore this tab.";
  }
});

loadDetails().catch(() => {
  summary.textContent = "The original page has been unloaded to free memory.";
});
