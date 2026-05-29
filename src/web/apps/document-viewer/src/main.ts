const root = document.getElementById("app");
if (!root) throw new Error("#app element not found");

interface WebView2Bridge {
  postMessage(message: string): void;
}

function getWebView(): WebView2Bridge | undefined {
  return (window as unknown as { chrome?: { webview?: WebView2Bridge } }).chrome?.webview;
}

function postToHost(message: object): void {
  getWebView()?.postMessage(JSON.stringify(message));
}

function mountStartupShell(target: HTMLElement): void {
  target.className = "document-viewer document-viewer--shell";

  const toolbar = document.createElement("div");
  toolbar.className = "toolbar toolbar--shell";

  const placeholder = document.createElement("div");
  placeholder.className = "viewer viewer--shell";

  const message = document.createElement("div");
  message.className = "viewer__placeholder";
  message.textContent = "DocuLink Initializing...";
  placeholder.appendChild(message);

  target.replaceChildren(toolbar, placeholder);
}

function afterNextPaint(callback: () => void): void {
  requestAnimationFrame(() => requestAnimationFrame(callback));
}

mountStartupShell(root);

afterNextPaint(() => {
  postToHost({ type: "viewer-shell-ready" });
  void import("./app.js").then(({ mountApp }) => {
    mountApp(root);
  });
});
