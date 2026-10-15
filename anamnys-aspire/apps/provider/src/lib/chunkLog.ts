// Every chunk of a recording, kept in memory until finalize is acknowledged, so a dropped
// connection can resend from the last chunk the server accepted (spec §6).
export class ChunkLog {
  private chunks: Uint8Array[] = [];
  private acked = -1;

  get length(): number { return this.chunks.length; }
  get lastAcked(): number { return this.acked; }
  get bytes(): number { return this.chunks.reduce((n, c) => n + c.byteLength, 0); }

  add(bytes: Uint8Array): number {
    this.chunks.push(bytes);
    return this.chunks.length - 1;
  }

  ack(seq: number): void { this.acked = Math.max(this.acked, seq); }

  // The server's view wins: after a restart or sweep it may know fewer chunks than we acked.
  rewindTo(seq: number): void { this.acked = Math.min(seq, this.chunks.length - 1); }

  // After buffer_expired the server starts from scratch.
  resetAcks(): void { this.acked = -1; }

  // Drops chunks the server refused (over the length limit); everything up to seq is kept.
  truncateAfter(seq: number): void {
    this.chunks.slice(seq + 1).forEach((c) => c.fill(0));
    this.chunks = this.chunks.slice(0, seq + 1);
    this.acked = Math.min(this.acked, seq);
  }

  pending(): { seq: number; bytes: Uint8Array }[] {
    return this.chunks.slice(this.acked + 1).map((bytes, i) => ({ seq: this.acked + 1 + i, bytes }));
  }

  clear(): void {
    this.chunks.forEach((c) => c.fill(0));
    this.chunks = [];
    this.acked = -1;
  }
}
