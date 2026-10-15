// Converts microphone audio to PCM 16 kHz mono Int16 LE and posts ~16 KB chunks plus a level.
// design/specs/2026-10-10-live-session-transcription-design.md §6.
const TARGET_RATE = 16000;
const CHUNK_SAMPLES = 8192; // 16 KB of Int16
const LEVEL_INTERVAL_S = 0.05;

class Pcm16Recorder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / TARGET_RATE;
    this.acc = 0;
    this.accCount = 0;
    this.pos = 0;
    this.out = new Int16Array(CHUNK_SAMPLES);
    this.filled = 0;
    this.paused = false;
    this.levelPeak = 0;
    this.levelFrames = 0;
    this.port.onmessage = (e) => {
      if (e.data === "pause") this.paused = true;
      else if (e.data === "resume") this.paused = false;
      else if (e.data === "flush") this.flush();
    };
  }

  flush() {
    if (this.filled > 0) {
      const chunk = this.out.slice(0, this.filled);
      this.port.postMessage({ type: "chunk", buffer: chunk.buffer }, [chunk.buffer]);
      this.filled = 0;
    }
    this.port.postMessage({ type: "flushed" });
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel || this.paused) return true;
    for (let i = 0; i < channel.length; i++) {
      const s = channel[i];
      this.levelPeak = Math.max(this.levelPeak, Math.abs(s));
      this.acc += s;
      this.accCount++;
      this.pos += 1;
      if (this.pos >= this.ratio) {
        this.pos -= this.ratio;
        const v = Math.max(-1, Math.min(1, this.acc / this.accCount));
        this.out[this.filled++] = v < 0 ? v * 0x8000 : v * 0x7fff;
        this.acc = 0;
        this.accCount = 0;
        if (this.filled === CHUNK_SAMPLES) {
          const chunk = this.out.slice();
          this.port.postMessage({ type: "chunk", buffer: chunk.buffer }, [chunk.buffer]);
          this.filled = 0;
        }
      }
    }
    this.levelFrames += channel.length;
    if (this.levelFrames >= sampleRate * LEVEL_INTERVAL_S) {
      this.port.postMessage({ type: "level", peak: this.levelPeak });
      this.levelPeak = 0;
      this.levelFrames = 0;
    }
    return true;
  }
}

registerProcessor("pcm16-recorder", Pcm16Recorder);
