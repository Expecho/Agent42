/* Agent 42 — hands-free voice conversation (ADR 0037/0038). Split out of app.js (ADR 0041). */

import { input, stopBtn, voiceBtn } from "./dom.js";
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
//
// Half-duplex over speakers (ADR 0079): on speakers the mic hears the reply and the recognizer
// transcribes it, so the loop talks to itself. The mic is therefore CLOSED from auto-send until the
// reply has finished speaking, and the reply is cut short by a gesture instead — Esc, the composer's
// stop button (ADR 0078) or the voice button. On a HEADSET there is no acoustic path back into the
// mic, so the mic stays live and ADR 0037's spoken barge-in is used unchanged. Which of the two runs
// is decided per turn by outputIsHeadset, and the unknown answer is "speakers" — muting a headset
// user costs one gesture, not muting a speaker user costs the feature.

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
    let rearmTimer = null;          // the chained re-arm retry (see maybeArmListening)
    let speakBuf = "";              // reply text not yet flushed to a complete-sentence utterance
    let spokenText = "";            // reply text already handed to synthesis this turn (echo ref)
    let lastSubmitted = "";         // last auto-sent utterance (so its trailing result ≠ barge-in)
    let interrupted = false;        // set by barge-in so the aborted turn's tail is not spoken
    let muted = false;              // recognition deliberately closed for this turn (speaker half-duplex)

    // Server-side TTS playback (ADR 0038). speechAbort cancels in-flight /api/speech fetches on barge-in;
    // speechGen invalidates any playback from a superseded turn; audioQueue holds ordered pending sentences
    // (each a Promise resolving to a playable item), so fetches pipeline while playback stays sequential.
    let speechAbort = null;
    let speechGen = 0;
    const audioQueue = [];
    let audioBusy = false;          // the playback pump loop is running
    let audioEl = null;             // the HTMLAudioElement currently playing, if any
    let audioResolve = null;        // resolver of the current chunk's playback promise (so cancel can end it)

    // ── Where is the reply coming out of? (ADR 0079) ──────────────────────────────────────────────
    // The property that actually matters is not "Bluetooth", it is "can the microphone hear it" — a
    // Bluetooth SPEAKER echoes exactly like a laptop speaker, while wired earbuds echo like neither.
    // So the test is headset-ness, which happens to cover every Bluetooth headset.
    //
    // The platform gives no "current output device" API: enumerateDevices() lists what EXISTS, and only
    // the entry whose deviceId is "default" tells us where sound is actually going. Labels are the only
    // signal, they are OS- and locale-worded, and they are blank until the page holds a media permission.
    // Every one of those is a way to be wrong, so a miss means "speakers" and the loop stays half-duplex.
    const HEADSET_LABEL = /bluetooth|headset|headphone|earphone|earbud|\bbuds\b|airpod|hands.?free/i;

    let outputIsHeadset = false;
    let unlockedLabels = false;

    // Re-read the default output device. Cached, because the decision is needed synchronously at
    // auto-send; refreshed at voice start and on every devicechange, which is what covers plugging a
    // headset in mid-conversation.
    async function refreshOutputKind() {
      try {
        const md = navigator.mediaDevices;
        if (!md?.enumerateDevices) { outputIsHeadset = false; return; }
        let outputs = (await md.enumerateDevices()).filter((d) => d.kind === "audiooutput");

        // Chrome blanks every label until the origin holds a media permission. Voice mode already has
        // the mic, so re-asking grants silently and without a prompt; the stream is opened only to make
        // the labels readable and is closed on the next line. Tried once — if it does not work here it
        // will not work later, and a second prompt is not worth a heuristic.
        if (!unlockedLabels && outputs.length && outputs.every((d) => !d.label)) {
          unlockedLabels = true;
          const stream = await md.getUserMedia({ audio: true });
          stream.getTracks().forEach((t) => t.stop());
          outputs = (await md.enumerateDevices()).filter((d) => d.kind === "audiooutput");
        }

        // Strictly the default device. Matching ANY listed output would keep the mic live whenever a
        // headset is merely CONNECTED, while the sound is still coming out of the speakers.
        const current = outputs.find((d) => d.deviceId === "default") || outputs[0];
        outputIsHeadset = HEADSET_LABEL.test(current?.label || "");
        // The one thing a reader cannot guess from here is what their own OS calls the device, and that
        // is exactly what decides the behaviour. Left in deliberately (ADR 0079's open residual).
        console.debug("[voice] audio output:", current?.label || "(no label)", "→ headset:", outputIsHeadset);
      } catch {
        outputIsHeadset = false; // permission refused, no API, anything at all → assume speakers
      }
    }

    navigator.mediaDevices?.addEventListener?.("devicechange", refreshOutputKind);

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

    // The button's second job (ADR 0079) is discoverable only if it says so: while a reply is running it
    // cuts the reply short instead of leaving voice mode. The resting text is read from the markup rather
    // than repeated here, so index.html stays the one place it is written.
    const idleTitle = voiceBtn.title;
    const busyTitle = "Stop the reply and listen again (Esc)";

    function setUi() {
      voiceBtn.setAttribute("aria-pressed", active ? "true" : "false");
      if (active) voiceBtn.dataset.state = state; else delete voiceBtn.dataset.state;
      const busy = active && (state === "speaking" || state === "thinking");
      voiceBtn.title = busy ? busyTitle : idleTitle;
    }

    function resetTurn() {
      finalText = ""; finalBuf = "";
      speakBuf = ""; spokenText = "";
      clearTimeout(silenceTimer);
    }

    // Close the mic for the rest of this turn (ADR 0079). abort(), not stop(): stop() finalizes and can
    // still deliver the trailing result of the utterance just sent, which is one of the two things being
    // silenced here. The 'end' it fires must not re-open the mic, hence the flag is set first.
    function muteMic() {
      if (muted) return;
      muted = true;
      try { vr.abort(); } catch { /* not started */ }
    }

    // Re-open it. start() throws when the abort's 'end' has not landed yet and the engine still considers
    // itself running — harmless, because that 'end' is still coming and its handler now sees muted=false
    // and starts the mic itself. That handler is the safety net for this throw, not just a keep-alive.
    function unmuteMic() {
      if (!muted) return;
      muted = false;
      if (active) { try { vr.start(); } catch { /* already started */ } }
    }

    function armListening() {
      state = "listening";
      resetTurn();
      unmuteMic();
      setUi();
    }

    // True while anything is (or is about to be) speaking — browser synth OR the server-audio pump/queue.
    function isSpeaking() {
      return synth.speaking || synth.pending || audioBusy || audioQueue.length > 0;
    }

    // Re-arm the mic for the next turn once the reply has fully finished speaking.
    //
    // It RETRIES rather than giving up, which it did not have to do before ADR 0079. When the mic stayed
    // live, losing this race just left the state label wrong and the user could still talk; now the mic
    // is shut until something re-arms it, so a turn that ends with nothing to speak — an empty reply, an
    // error, a stream that settles after this runs — would leave the loop deaf with no way back except
    // the buttons. One chained timer, cancelled by the next call, so overlapping utterance ends cannot
    // stack up pollers.
    function maybeArmListening() {
      if (!active || state === "listening") return;
      clearTimeout(rearmTimer);
      if (streaming || isSpeaking()) { rearmTimer = setTimeout(maybeArmListening, 150); return; }
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

    // Stop the reply being SPOKEN and hand the mic straight back, without touching the server. This is
    // the half the composer's stop button needs: it already stops generation (ADR 0078), but nothing
    // there knows that a reply is still being read aloud out of a queue of already-fetched audio.
    function silenceAndRearm() {
      if (!active || (state !== "speaking" && state !== "thinking")) return false;
      interrupted = true; // the turn's tail must not be spoken when the stream ends
      cancelSpeech();
      armListening(); // re-opens the mic when this turn muted it
      return true;
    }

    // What replaces talking over the reply when the mic is closed (ADR 0079): Esc, the stop button, or
    // the voice button. Stops the speaking AND the generation — unlike the stop button's own handler,
    // this one is reached from controls that mean "never mind", so both halves end.
    function interrupt() {
      if (!silenceAndRearm()) return false;
      stopGeneration({ abortLocally: true });
      return true;
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
      // On speakers the mic closes HERE, before a single token has arrived, not when speech starts: the
      // trailing final of the utterance just sent lands during "thinking" and is the other half of the
      // loop talking to itself. On a headset it stays open and barge-in works as it always did.
      if (!outputIsHeadset) muteMic();
      refreshOutputKind(); // for the NEXT turn; this one already decided
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
        // Muted turns should never get here — abort() discards what was buffered — but a result already
        // dispatched when the mic closed would be the reply itself, so it is dropped rather than trusted.
        if (muted) return;
        if (!isSelfOrEcho(heard)) bargeIn(heard); // genuine interruption (headset: the mic stayed live)
        return;
      }
      // listening: accumulate and restart the end-of-utterance countdown
      finalText += finals;
      finalBuf = (finalText + interim).trim();
      restartSilence();
    });

    // Chrome ends continuous recognition on silence/timeout; keep the mic alive while active — but NOT
    // while muted, or the restart would immediately undo the mute and the loop would hear itself again.
    vr.addEventListener("end", () => { if (active && !muted) { try { vr.start(); } catch { /* race */ } } });

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
      muted = false;
      cancelSpeech(); // initialize speechAbort and clear any stale audio state
      refreshOutputKind(); // the first turn decides on whatever this resolves to, default speakers
      vr.lang = profile.preferredLanguage || navigator.language;
      armListening();
      try { vr.start(); } catch { /* start() throws if already started */ }
    }

    function stopVoice() {
      if (!active) return;
      active = false;
      state = "idle";
      muted = false;
      clearTimeout(silenceTimer);
      clearTimeout(rearmTimer);
      cancelSpeech(); // stop browser voice AND server audio
      if (sendAbort) sendAbort.abort();
      try { vr.stop(); } catch { /* not started */ }
      resetTurn();
      setUi();
    }

    // While a reply is running the button means "never mind" — the gesture that replaces talking over it
    // when the mic is shut. A second tap then exits, so leaving voice mode mid-reply costs two taps and
    // the first one is the one anybody reaching for the button in that moment actually wants.
    voiceBtn.addEventListener("click", () => {
      if (interrupt()) return;
      if (active) stopVoice(); else startVoice();
    });

    // The composer's stop button already stops the server (ADR 0078); voice mode adds the speaking half,
    // or a stopped turn would keep being read aloud from audio already fetched. Deliberately not
    // interrupt(): send.js's own handler is what calls stopGeneration, and two calls means two POSTs.
    stopBtn.addEventListener("click", () => { silenceAndRearm(); });

    // Esc, for the hands that are already on the keyboard. Narrow on purpose — it does nothing unless a
    // reply is actually running — so the drawer, search and usage Escape handlers keep their meaning.
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") interrupt(); });

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
