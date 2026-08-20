/* Agent 42 — speech input — Web Speech API dictation (ADR 0006). Split out of app.js (ADR 0041). */

import { autosize } from "./composer.js";
import { input, micBtn } from "./dom.js";
import { profile } from "./profile.js";
import { setStopSpeech } from "./send.js";

// Vendor-prefixed in Chrome/Edge; absent in Firefox. Transcription runs on the browser
// vendor's cloud (ADR 0006), not in the app's Azure tenant. Requires a secure context —
// prod is https and localhost counts, so DevHost exercises it with a real mic.
export const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;

export function wireSpeech() {
  if (!SpeechRecognition) {
    micBtn.hidden = true; // graceful degradation: no dictation, no button, no wiring
  } else {
    // Chrome on Android flags every interim growth stage isFinal: true while reporting
    // confidence: 0; trusting the flag re-appends each stage to the permanent accumulator
    // ("Hoe" + "Hoe laat" + …), the double-output bug on Android PWAs. Distrust unscored
    // finals THERE ONLY — desktop engines can report confidence 0 on a genuine final, and
    // demoting it there would silently drop the segment. UA check, mirroring react-speech-recognition.
    const distrustZeroConfidenceFinals = /android/i.test(navigator.userAgent);

    const recognition = new SpeechRecognition();
    recognition.continuous = true;
    recognition.interimResults = true;

    const defaultPlaceholder = input.placeholder; // restored when a new dictation starts

    // Text already in the box when dictation began; the transcript is appended to it so a
    // dictated phrase never clobbers what the user typed first.
    let baseText = "";
    // Finalized transcript accumulated across result events. In continuous mode each phrase
    // lands at its own result index, so finals must be folded in incrementally (once each) —
    // rebuilding from the whole results list every event stacks every interim snapshot.
    let finalTranscript = "";
    // True between start and end. Guards a late result fired after a stop from repopulating
    // an already-cleared textarea (see stopSpeech / send).
    let recognizing = false;

    function setRecordingUi(on) {
      micBtn.setAttribute("aria-pressed", on ? "true" : "false");
    }

    // Only walk the results changed in THIS event (from resultIndex): fold newly-finalized
    // segments into finalTranscript once, and rebuild just the trailing interim live. A final
    // is trusted only when the engine actually scored it (confidence > 0) on Android — an
    // unscored "final" is really an interim growth stage, so it must fall through to the
    // self-correcting interim branch, not the permanent accumulator. Flat by design
    // (cognitive-complexity gate).
    recognition.addEventListener("result", (event) => {
      if (!recognizing) return; // ignore trailing results after a stop
      let interim = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const res = event.results[i];
        const trustworthyFinal =
          res.isFinal && (!distrustZeroConfidenceFinals || (res[0].confidence ?? 0) > 0);
        if (trustworthyFinal) finalTranscript += res[0].transcript;
        else interim += res[0].transcript;
      }
      input.value = baseText + finalTranscript + interim;
      autosize(); // programmatic value writes don't fire the "input" event
      input.selectionStart = input.selectionEnd = input.value.length; // caret at end
    });

    recognition.addEventListener("start", () => {
      recognizing = true;
      setRecordingUi(true);
    });

    recognition.addEventListener("end", () => {
      // Fires on manual stop and on Chrome's silence/timeout auto-stop; reset either way.
      recognizing = false;
      setRecordingUi(false);
      input.focus();
    });

    recognition.addEventListener("error", (event) => {
      recognizing = false;
      setRecordingUi(false);
      if (event.error === "not-allowed" || event.error === "service-not-allowed") {
        // Mic permission denied/blocked — subtle one-shot hint, no intrusive alert.
        input.placeholder = "Microphone access blocked — check browser permissions.";
      }
    });

    micBtn.addEventListener("click", () => {
      if (recognizing) {
        recognition.stop();
        return;
      }
      baseText = input.value; // append to whatever is already in the box
      finalTranscript = ""; // fresh session — don't carry a prior dictation's finals
      input.placeholder = defaultPlaceholder; // clear any stale "mic blocked" hint
      // Read the cached profile at start (no re-fetch); fall back to the device language.
      recognition.lang = profile.preferredLanguage || navigator.language;
      try {
        recognition.start();
      } catch { /* start() throws if already started; ignore the double-tap race */ }
    });

    // Idempotent stop, wired into send(). Mutes late results synchronously (recognizing =
    // false) before send() clears the textarea, then stops the engine and resets the mic.
    setStopSpeech(() => {
      if (!recognizing) return;
      recognizing = false;
      recognition.stop();
    });
  }
}
