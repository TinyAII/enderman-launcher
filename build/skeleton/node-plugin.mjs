import { createInterface } from 'node:readline';

// 最小进程外插件：JSON-RPC 2.0 over stdio，一行一帧。
// 骨架阶段只实现握手与回显，用来验证宿主侧的协议、超时和进程生命周期。
const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

rl.on('line', (line) => {
  const text = line.trim();
  if (!text) return;

  let req;
  try {
    req = JSON.parse(text);
  } catch {
    process.stdout.write(JSON.stringify({
      jsonrpc: '2.0', id: null,
      error: { code: -32700, message: 'parse error' }
    }) + '\n');
    return;
  }

  if (req.method === 'handshake') {
    process.stdout.write(JSON.stringify({
      jsonrpc: '2.0',
      id: req.id,
      result: {
        alive: true,
        pid: process.pid,
        node: process.version,
        pluginApiVersion: '0.1.0-skeleton'
      }
    }) + '\n');
    return;
  }

  process.stdout.write(JSON.stringify({
    jsonrpc: '2.0', id: req.id,
    error: { code: -32601, message: `method not found: ${req.method}` }
  }) + '\n');
});

rl.on('close', () => process.exit(0));
