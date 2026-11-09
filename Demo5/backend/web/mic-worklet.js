// Turns mic input into 20 ms PCM16 chunks at 16 kHz, each stamped with its AudioContext time
// (the same clock the metronome is scheduled on, so the server's beat grid lines up).
//
// The AudioContext runs at the device rate (usually 48 kHz) so playback stays full-band;
// this node low-passes and resamples the mic down to what Whisper wants.
const CHUNK = 320;

class Biquad {   // RBJ low-pass
  constructor(fs, f0, q) {
    const w = 2 * Math.PI * f0 / fs, a = Math.sin(w) / (2 * q), c = Math.cos(w), a0 = 1 + a;
    this.b0 = (1 - c) / 2 / a0; this.b1 = (1 - c) / a0; this.b2 = this.b0;
    this.a1 = -2 * c / a0; this.a2 = (1 - a) / a0;
    this.x1 = this.x2 = this.y1 = this.y2 = 0;
  }
  step(x) {
    const y = this.b0 * x + this.b1 * this.x1 + this.b2 * this.x2 - this.a1 * this.y1 - this.a2 * this.y2;
    this.x2 = this.x1; this.x1 = x; this.y2 = this.y1; this.y1 = y;
    return y;
  }
}

class MicChunker extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.target = (options.processorOptions && options.processorOptions.targetRate) || sampleRate;
    this.step = sampleRate / this.target;          // input samples per output sample
    const cutoff = Math.min(0.45 * this.target, 0.45 * sampleRate);
    this.filters = this.step > 1 ? [new Biquad(sampleRate, cutoff, 0.54), new Biquad(sampleRate, cutoff, 1.31)] : [];
    this.prev = 0;
    this.phase = 1;                                // next output position, in input samples after `prev`
    this.buf = new Int16Array(CHUNK);
    this.n = 0;
    this.t = 0;
  }

  emit(v, t) {
    if (this.n === 0) this.t = t;
    v = Math.max(-1, Math.min(1, v));
    this.buf[this.n++] = v < 0 ? v * 32768 : v * 32767;
    if (this.n === CHUNK) {
      this.port.postMessage({ t: this.t, pcm: this.buf.buffer }, [this.buf.buffer]);
      this.buf = new Int16Array(CHUNK);
      this.n = 0;
    }
  }

  process(inputs) {
    // Keep the timeline continuous even if the input briefly has no data.
    const ch = inputs[0] && inputs[0][0] ? inputs[0][0] : new Float32Array(128);
    for (let i = 0; i < ch.length; i++) {
      let y = ch[i];
      for (const f of this.filters) y = f.step(y);
      // Linear interpolation between the previous and current filtered input sample.
      while (this.phase <= 1) {
        this.emit(this.prev + (y - this.prev) * this.phase, currentTime + (i - 1 + this.phase) / sampleRate);
        this.phase += this.step;
      }
      this.phase -= 1;
      this.prev = y;
    }
    return true;
  }
}

registerProcessor("mic-chunker", MicChunker);
