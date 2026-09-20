/* Agent 42 — hands-free voice conversation (ADR 0037/0038). Split out of app.js (ADR 0041). */

import { input, voiceBtn } from "./dom.js";
import { profile } from "./profile.js";
import { userDefaultSpeechModel } from "./providers.js";
import { send, sendAbort, setVoice, stopGeneration, stopSpeech, streaming } from "./send.js";
import { SpeechRecognition } from "./speech.js";

// Extends dictation (ADR 0006): SpeechRecognition to listen + speechSynthesis to speak, in a
// hands-free loop (listen → auto-send → speak → interrupt). A DEDICATED recognition instance —
// never the dictation one above — so its delicate accumulation logic stays untouched; the two
// mic uses are mutually exclusive. Needs both APIs; where either is absent the button is hidden
// (graceful degradation, mirroring the mic). speechSynthesis output is a new egress-free path:
// the reply text never leaves the browser (ADR 0013 unchanged); recognition still uses the
// vendor's cloud like dictation (ADR 0006's accepted residual).
//
// Server-side voice (ADR 0038): when the user has picked a default speech model
// (userDefaultSpeechModel), the reply is spoken by POSTing each sentence to /api/speech and playing
// the returned audio (a natural Azure/OpenAI neural voice) instead of speechSynthesis — which DOES
// send the reply text outside the tenant (ADR 0013 egress), the reason it is opt-in. With none set,
// or on any /api/speech failure, it falls back to the browser voice. Barge-in works either way.

export function wireVoice() {
  if (!SpeechRecognition || !window.speechSynthesis) {
    voiceBtn.hidden = true;
  } else {
    const SILENCE_MS = 1200; // end-of-utterance debounce: auto-send this long after speech stops
    const synth = window.speechSynthesis;
    const vr = new SpeechRecognition();
    vr.continuous = true;
    vr.interimResults = true;

    // Chrome on Android flags every interim growth stage isFinal: true while reporting confidence: 0;
    // trusting the flag re-appends each stage to finalText ("Hoe" + "Hoe laat" + …), the double-output
    // bug — here it duplicates the AUTO-SENT prompt. Distrust unscored finals THERE ONLY (desktop engines
    // can report confidence 0 on a genuine final). Same fix as the dictation instance (ADR 0006).
    const distrustZeroConfidenceFinals = /android/i.test(navigator.userAgent);

    let active = false;             // voice mode on/off
    let state = "idle";             // idle | listening | thinking | speaking
    let finalText = "";             // finalized transcript of the in-progress listening utterance
    let finalBuf = "";              // finalText + trailing interim, the candidate to auto-send
    let silenceTimer = null;
    let speakBuf = "";              // reply text not yet flushed to a complete-sentence utterance
    let spokenText = "";            // reply text already handed to synthesis this turn (echo ref)
    let lastSubmitted = "";         // last auto-sent utterance (so its trailing result ≠ barge-in)
    let interrupted = false;        // set by barge-in so the aborted turn's tail is not spoken

    // Server-side TTS playback (ADR 0038). speechAbort cancels in-flight /api/speech fetches on barge-in;
    // speechGen invalidates any playback from a superseded turn; audioQueue holds ordered pending sentences
    // (each a Promise resolving to a playable item), so fetches pipeline while playback stays sequential.
    let speechAbort = null;
    let speechGen = 0;
    const audioQueue = [];
    let audioBusy = false;          // the playback pump loop is running
    let audioEl = null;             // the HTMLAudioElement currently playing, if any
    let audioResolve = null;        // resolver of the current chunk's playback promise (so cancel can end it)

    // Voices load asynchronously; refresh the cache on change and pick the best lang match.
    let voices = synth.getVoices();
    synth.addEventListener("voiceschanged", () => { voices = synth.getVoices(); });
    function pickVoice(lang) {
      const l = (lang || "").toLowerCase();
      if (!voices.length || !l) return null;
      return voices.find((v) => (v.lang || "").toLowerCase() === l)
        || voices.find((v) => (v.lang || "").toLowerCase().startsWith(l.slice(0, 2)))
        || null;
    }

    // Strip the common markdown marks so TTS never reads "asterisk asterisk" / a URL aloud.
    function stripMarkdown(s) {
      return s
        .replace(/```[\s\S]*?```/g, " ")
        .replace(/`([^`]+)`/g, "$1")
        .replace(/!\[[^\]]*\]\([^)]*\)/g, " ")
        .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
        .replace(/[*_#>~]+/g, " ")
        .replace(/\s+/g, " ")
        .trim();
    }

    function setUi() {
      voiceBtn.setAttribute("aria-pressed", active ? "true" : "false");
      if (active) voiceBtn.dataset.state = state; else delete voiceBtn.dataset.state;
    }

    function resetTurn() {
      finalText = ""; finalBuf = "";
      speakBuf = ""; spokenText = "";
      clearTimeout(silenceTimer);
    }

    function armListening() {
      state = "listening";
      resetTurn();
      setUi();
    }

    // True while anything is (or is about to be) speaking — browser synth OR the server-audio pump/queue.
    function isSpeaking() {
      return synth.speaking || synth.pending || audioBusy || audioQueue.length > 0;
    }

    // Re-arm the mic for the next turn once the reply has fully finished speaking.
    function maybeArmListening() {
      if (!active || state === "listening") return;
      if (streaming || isSpeaking()) return;
      armListening();
    }

    // Cancels all speech NOW — server audio (abort in-flight fetches, drop the queue, stop playback) and
    // browser synth — and bumps speechGen so any superseded playback bails. Called on barge-in, teardown,
    // and at each turn start (a fresh epoch). Re-arms speechAbort for the next turn's fetches.
    function cancelSpeech() {
      speechGen++;
      if (speechAbort) { try { speechAbort.abort(); } catch { /* already aborted */ } }
      speechAbort = new AbortController();
      audioQueue.length = 0;
      if (audioEl) { try { audioEl.pause(); } catch { /* nothing playing */ } audioEl = null; }
      try { synth.cancel(); } catch { /* nothing speaking */ }
      if (audioResolve) { const r = audioResolve; audioResolve = null; r(); }
    }

    // Speak one sentence. Server-side (ADR 0038) when a speech model is picked, else the browser voice.
    function speak(text) {
      const clean = stripMarkdown(text);
      if (!clean) return;
      spokenText += " " + clean; // echo reference — populated before playback so a barge-in can compare
      if (userDefaultSpeechModel) { serverSpeak(clean); return; }
      browserSpeak(clean);
    }

    // Browser-voice path (no speech model): a self-contained utterance that re-arms on its own end.
    function browserSpeak(text) {
      const u = new SpeechSynthesisUtterance(text);
      u.lang = vr.lang || navigator.language;
      const v = pickVoice(u.lang);
      if (v) u.voice = v;
      u.addEventListener("start", () => { if (active) { state = "speaking"; setUi(); } });
      u.addEventListener("end", maybeArmListening);
      u.addEventListener("error", maybeArmListening);
      synth.speak(u);
    }

    // Server-voice path: start the fetch NOW (fetches pipeline), enqueue it for in-order playback, pump.
    function serverSpeak(text) {
      audioQueue.push(fetchSpeech(text, speechAbort));
      pump();
    }

    // Fetch synthesized audio for one sentence. Resolves to a playable item: {url} on success, {fallback}
    // on a server/HTTP error (→ browser voice for this sentence), or null when aborted (barge-in).
    async function fetchSpeech(text, abort) {
      try {
        const res = await fetch("/api/speech", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ text }),
          signal: abort ? abort.signal : undefined,
        });
        if (!res.ok) return { fallback: text }; // 409 (no model) or error → browser voice
        return { url: URL.createObjectURL(await res.blob()) };
      } catch (e) {
        return e.name === "AbortError" ? null : { fallback: text };
      }
    }

    // Plays the queued sentences strictly in order. One pump loop at a time; a superseded turn (speechGen
    // changed) or a stop bails. Arms listening when the queue drains.
    function pump() {
      if (audioBusy) return;
      audioBusy = true;
      const gen = speechGen;
      (async () => {
        while (audioQueue.length && gen === speechGen && active) {
          const item = await audioQueue.shift();
          if (gen !== speechGen || !active || !item) continue;
          state = "speaking"; setUi();
          await (item.fallback != null ? synthPlay(item.fallback) : playUrl(item.url));
        }
        audioBusy = false;
        maybeArmListening();
      })();
    }

    // Plays one audio URL, resolving on end/error (or when cancelSpeech resolves it). Revokes the blob URL.
    function playUrl(url) {
      return new Promise((resolve) => {
        const el = new Audio(url);
        audioEl = el;
        audioResolve = resolve;
        const done = () => {
          try { URL.revokeObjectURL(url); } catch { /* already revoked */ }
          if (audioEl === el) audioEl = null;
          if (audioResolve === resolve) audioResolve = null;
          resolve();
        };
        el.addEventListener("ended", done);
        el.addEventListener("error", done);
        el.play().catch(done); // autoplay policy / decode failure — move on rather than stall
      });
    }

    // Browser-voice fallback for one sentence INSIDE the pump — a promise so playback stays sequential.
    function synthPlay(text) {
      return new Promise((resolve) => {
        audioResolve = resolve;
        const finish = () => { if (audioResolve === resolve) audioResolve = null; resolve(); };
        const u = new SpeechSynthesisUtterance(text);
        u.lang = vr.lang || navigator.language;
        const v = pickVoice(u.lang);
        if (v) u.voice = v;
        u.addEventListener("end", finish);
        u.addEventListener("error", finish);
        synth.speak(u);
      });
    }

    // Flush complete sentences from the delta buffer (or everything, at stream end) so speech
    // starts before the whole reply has arrived. Splits on the last sentence terminator.
    function flush(all) {
      if (all) {
        const rest = speakBuf; speakBuf = "";
        speak(rest);
        return;
      }
      let cut = -1;
      for (let i = 0; i < speakBuf.length; i++) {
        const c = speakBuf[i];
        if (c === "." || c === "!" || c === "?" || c === "\n") cut = i;
      }
      if (cut < 0) return;
      const chunk = speakBuf.slice(0, cut + 1);
      speakBuf = speakBuf.slice(cut + 1);
      speak(chunk);
    }

    // A recognition result while the assistant is (or was just) talking is usually the mic
    // hearing the TTS, or the trailing final of what we just sent — not a real interruption.
    // Treat it as self/echo when most of its words already appear in what we're speaking or
    // just submitted. Best-effort: headphones remain the clean guarantee (ADR 0037).
    function isSelfOrEcho(text) {
      const words = text.toLowerCase().match(/\p{L}+/gu) || [];
      if (!words.length) return true;
      const ref = (spokenText + " " + lastSubmitted).toLowerCase();
      const hits = words.filter((w) => ref.includes(w)).length;
      return hits / words.length >= 0.6;
    }

    function bargeIn(heard) {
      interrupted = true;
      cancelSpeech(); // stop the reply mid-word — browser voice or server audio
      // Both halves, and neither is redundant. The local abort is what makes the interruption immediate:
      // onDelta speaks whatever it is handed and is NOT gated by `interrupted`, so waiting for a round trip
      // would keep reading buffered text over the user. The server stop (ADR 0078) is what makes the stored
      // turn honest: without it the run continues and the conversation keeps a reply nobody ever heard.
      // Fire-and-forget — nothing below waits on the network.
      stopGeneration({ abortLocally: true });
      state = "listening";
      resetTurn();
      finalText = heard;
      finalBuf = heard.trim();
      setUi();
      restartSilence();
    }

    function restartSilence() {
      clearTimeout(silenceTimer);
      silenceTimer = setTimeout(trySubmit, SILENCE_MS);
    }

    // Auto-send the accumulated utterance. Defers while a turn is still unwinding (a barge-in
    // aborts the previous fetch; send()'s finally clears `streaming` a tick later).
    function trySubmit() {
      if (!active || state !== "listening") return;
      const t = finalBuf.trim();
      if (!t) return;
      if (streaming) { setTimeout(trySubmit, 150); return; }
      lastSubmitted = t;
      interrupted = false;
      state = "thinking";
      resetTurn();
      cancelSpeech(); // fresh speech epoch for this turn (clears any lingering audio)
      setUi();
      send(t); // fire-and-forget: onDelta speaks it, onStreamEnd flushes the tail
    }

    vr.addEventListener("result", (event) => {
      if (!active) return;
      let finals = "", interim = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const r = event.results[i];
        // A final is trusted only when the engine actually scored it (confidence > 0) on Android — an
        // unscored "final" is really an interim growth stage, so it falls through to the self-correcting
        // interim branch instead of stacking onto finalText (the Android double-output bug, ADR 0006).
        const trustworthyFinal = r.isFinal && (!distrustZeroConfidenceFinals || (r[0].confidence ?? 0) > 0);
        if (trustworthyFinal) finals += r[0].transcript; else interim += r[0].transcript;
      }
      const heard = (finals + interim).trim();
      if (!heard) return;
      if (state === "speaking" || state === "thinking") {
        if (!isSelfOrEcho(heard)) bargeIn(heard); // genuine interruption
        return;
      }
      // listening: accumulate and restart the end-of-utterance countdown
      finalText += finals;
      finalBuf = (finalText + interim).trim();
      restartSilence();
    });

    // Chrome ends continuous recognition on silence/timeout; keep the mic alive while active.
    vr.addEventListener("end", () => { if (active) { try { vr.start(); } catch { /* race */ } } });

    vr.addEventListener("error", (event) => {
      if (event.error === "not-allowed" || event.error === "service-not-allowed") {
        stopVoice();
        input.placeholder = "Microphone access blocked — check browser permissions.";
      }
      // transient errors (no-speech, network): the 'end' handler restarts if still active
    });

    function startVoice() {
      if (active) return;
      stopSpeech(); // dictation and voice mode share the one mic — never both at once
      active = true;
      cancelSpeech(); // initialize speechAbort and clear any stale audio state
      vr.lang = profile.preferredLanguage || navigator.language;
      armListening();
      try { vr.start(); } catch { /* start() throws if already started */ }
    }

    function stopVoice() {
      if (!active) return;
      active = false;
      state = "idle";
      clearTimeout(silenceTimer);
      cancelSpeech(); // stop browser voice AND server audio
      if (sendAbort) sendAbort.abort();
      try { vr.stop(); } catch { /* not started */ }
      resetTurn();
      setUi();
    }

    voiceBtn.addEventListener("click", () => { if (active) stopVoice(); else startVoice(); });

    // Expose the hooks send() calls each turn (no-ops until a turn is in flight).
    setVoice({
      get active() { return active; },
      onDelta(text) { if (active) { speakBuf += text; flush(false); } },
      onStreamEnd() {
        if (!active) return;
        if (interrupted) { interrupted = false; return; } // aborted turn: don't speak its tail
        flush(true);
        setTimeout(maybeArmListening, 60); // empty/silent reply → re-arm without an utterance
      },
      stop: stopVoice,
    });
  }
}
