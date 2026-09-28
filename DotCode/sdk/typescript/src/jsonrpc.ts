import { EventEmitter } from "node:events";
import type { Readable, Writable } from "node:stream";

export class JsonRpcError extends Error {
  constructor(public readonly code: number, message: string) {
    super(message);
    this.name = "JsonRpcError";
  }
}

type RequestHandler = (method: string, params: any) => Promise<unknown>;

/** Newline-delimited JSON-RPC 2.0 peer (both directions: the server also sends requests to us). */
export class JsonRpcPeer extends EventEmitter {
  private nextId = 1;
  private buffer = "";
  private readonly pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>();
  onRequest?: RequestHandler;

  constructor(private readonly input: Readable, private readonly output: Writable) {
    super();
    input.setEncoding("utf8");
    input.on("data", (chunk: string) => this.onData(chunk));
    input.on("close", () => {
      for (const p of this.pending.values()) p.reject(new Error("DotCode server connection closed"));
      this.pending.clear();
      this.emit("close");
    });
  }

  private onData(chunk: string) {
    this.buffer += chunk;
    let nl: number;
    while ((nl = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, nl).trim();
      this.buffer = this.buffer.slice(nl + 1);
      if (line.length === 0) continue;
      let msg: any;
      try {
        msg = JSON.parse(line);
      } catch {
        continue;
      }
      this.dispatch(msg);
    }
  }

  private dispatch(msg: any) {
    if (msg.method === undefined) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new JsonRpcError(msg.error.code, msg.error.message));
      else p.resolve(msg.result);
      return;
    }
    if (msg.id === undefined || msg.id === null) {
      this.emit("notification", msg.method, msg.params);
      return;
    }
    const handler = this.onRequest;
    Promise.resolve()
      .then(() => (handler ? handler(msg.method, msg.params) : Promise.reject(new JsonRpcError(-32601, `Method not found: ${msg.method}`))))
      .then(
        (result) => this.write({ jsonrpc: "2.0", id: msg.id, result: result ?? null }),
        (err: any) => this.write({ jsonrpc: "2.0", id: msg.id, error: { code: err?.code ?? -32603, message: String(err?.message ?? err) } }),
      );
  }

  request<T = any>(method: string, params: unknown = {}): Promise<T> {
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.write({ jsonrpc: "2.0", id, method, params });
    });
  }

  notify(method: string, params: unknown = {}) {
    this.write({ jsonrpc: "2.0", method, params });
  }

  private write(msg: unknown) {
    this.output.write(JSON.stringify(msg) + "\n");
  }
}
