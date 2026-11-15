# Voice & speech

Dictate a message, or talk hands-free: the reply is spoken back and the app listens again.

## Dictate a message

Press the mic button (**Dictate prompt**) and speak. Your words appear in the composer. Review them and send yourself.

## Hands-free voice mode

Press the **Voice conversation** button. Agent42 then loops: it listens, sends your message after a short pause, speaks the reply, and listens again. Press the button again to leave voice mode.

To cut a reply short, press **Esc**, the **Stop generating** button, or the voice button. On a headset you can also just talk over the reply.

!!! note
    On speakers, the microphone closes while the reply plays so it doesn't hear itself. On a headset it stays open, which is what makes talking over the reply possible.

## Use a better voice

By default the browser's built-in voice reads replies. For a model-generated voice, set a **default speech model** in Preferences, in the model providers section. The model must be marked as a speech model in its provider settings. Each sentence is then sent to that model. If it fails, that sentence falls back to the browser voice.

## Privacy

Speech recognition is done by your browser, and no microphone audio is sent to Agent42. Your browser's own speech service may process audio according to the browser vendor's policy.

If you set a speech model, the reply text is sent to that model's provider to be spoken. With none set, nothing leaves the browser for speech.

## Good to know

- Spoken text skips markdown formatting, and reasoning text is never read aloud.
- Dictation and voice mode can't run at the same time.
- Voice features depend on the browser's speech support. Chrome works best.
