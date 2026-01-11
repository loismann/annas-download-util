const http = require('http');
const { URL } = require('url');

const apiProxyPort = parseInt(process.env.E2E_API_PROXY_PORT || '5001', 10);
const authProxyPort = parseInt(process.env.E2E_AUTH_PROXY_PORT || '5050', 10);
const apiTargetBase = parseInt(process.env.E2E_API_TARGET_BASE_PORT || '5101', 10);
const authTargetBase = parseInt(process.env.E2E_AUTH_TARGET_BASE_PORT || '5150', 10);

const hopByHopHeaders = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailers',
  'transfer-encoding',
  'upgrade',
]);

function getWorkerIndex(req) {
  const header = req.headers['x-e2e-worker'];
  const index = header ? parseInt(Array.isArray(header) ? header[0] : header, 10) : 0;
  return Number.isFinite(index) && index >= 0 ? index : 0;
}

function createProxyServer(listenPort, targetBasePort, label) {
  return http.createServer((req, res) => {
    const workerIndex = getWorkerIndex(req);
    const targetPort = targetBasePort + workerIndex;
    const targetUrl = new URL(req.url, `http://localhost:${targetPort}`);

    const headers = { ...req.headers };
    for (const header of Object.keys(headers)) {
      if (hopByHopHeaders.has(header.toLowerCase())) {
        delete headers[header];
      }
    }
    headers.host = `localhost:${targetPort}`;

    const proxyReq = http.request(
      {
        hostname: 'localhost',
        port: targetPort,
        path: targetUrl.pathname + targetUrl.search,
        method: req.method,
        headers,
      },
      proxyRes => {
        res.writeHead(proxyRes.statusCode || 500, proxyRes.headers);
        proxyRes.pipe(res, { end: true });
      }
    );

    proxyReq.on('error', err => {
      res.writeHead(502, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: `${label} proxy error`, detail: err.message }));
    });

    req.pipe(proxyReq, { end: true });
  }).listen(listenPort, () => {
    console.log(`[e2e-proxy] ${label} proxy listening on ${listenPort} -> base ${targetBasePort}`);
  });
}

const apiServer = createProxyServer(apiProxyPort, apiTargetBase, 'api');
const authServer = createProxyServer(authProxyPort, authTargetBase, 'auth');

function shutdown() {
  apiServer.close(() => {});
  authServer.close(() => {});
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
