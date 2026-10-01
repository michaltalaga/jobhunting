// Toolbar button: send the current tab, exactly as rendered (SPAs included), to the local job hunting server.
// The request goes from this service worker, not from the page, so the site's Content-Security-Policy
// cannot block it the way it would block a bookmarklet.
const SERVER = 'http://localhost:5317';

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: 'open-dashboard', title: 'Open job hunting dashboard', contexts: ['action'] });
});

chrome.contextMenus.onClicked.addListener((info) => {
  if (info.menuItemId === 'open-dashboard') chrome.tabs.create({ url: SERVER });
});

chrome.action.onClicked.addListener(async (tab) => {
  await badge(tab.id, '…', '#6b7280');
  try {
    const frames = await snapshotFrames(tab.id);
    const response = await fetch(`${SERVER}/api/jobs/capture`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url: tab.url, title: tab.title, frames }),
    });
    if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
    const { duplicateOf } = await response.json();
    if (duplicateOf.length > 0) await badge(tab.id, 'dup', '#d97706');
    else await badge(tab.id, '✓', '#16a34a');
  } catch (error) {
    console.error('Capture failed', error);
    await badge(tab.id, '✗', '#dc2626');
  }
});

/** Every frame's HTML and visible text; job boards often embed the advert in an iframe. */
async function snapshotFrames(tabId) {
  let results;
  try {
    results = await chrome.scripting.executeScript({ target: { tabId, allFrames: true }, func: snapshot });
  } catch {
    results = await chrome.scripting.executeScript({ target: { tabId }, func: snapshot });
  }
  return results.filter((r) => r.result).map((r) => ({ ...r.result, isTop: r.frameId === 0 }));
}

// Runs inside the page, once per frame.
function snapshot() {
  return {
    url: location.href,
    title: document.title,
    html: document.documentElement.outerHTML,
    text: document.body ? document.body.innerText : '',
  };
}

async function badge(tabId, text, color) {
  await chrome.action.setBadgeBackgroundColor({ tabId, color });
  await chrome.action.setBadgeText({ tabId, text });
  if (text !== '…') setTimeout(() => chrome.action.setBadgeText({ tabId, text: '' }), 5000);
}
