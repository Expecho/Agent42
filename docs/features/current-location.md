# Current location

The assistant can ask for your location to answer things like local weather, what's nearby, travel times or "where am I".

## Turn it on

1. Switch on the **Location** tool. See [Tool selection](tool-selection.md).
2. When your browser asks, allow location access for Agent 42.

The assistant only asks for your position when your question needs it. Nothing is sent just because the tool is on.

## What it shares

The assistant receives your coordinates and, when the operator has set up address lookup, a street address. Without address lookup the app still works and returns coordinates only.

- Your position is not saved as a setting.
- The model can't pick the place itself, so it can't be told to look up some other location.
- It isn't available in Copilot chats.

!!! note
    In a reminder or other background run there is no browser open to ask, so the assistant tells you it has no location.

## Troubleshooting

- **Location isn't shared**: allow location for the site in your browser settings, then ask again.
- **No fix**: the device couldn't get a position in time. Try again, ideally with Wi-Fi or GPS on.
- **Coordinates but no address**: address lookup isn't configured on this deployment.
- Your browser keeps a recent position for a short while, so repeated questions are answered quickly.

## For operators

Set the `GOOGLE_MAPS_API_KEY` secret to turn coordinates into street addresses.

The address goes along with the rest of the conversation to your model provider, and to long-term memory if that is on. See [Bring your own models](bring-your-own-models.md) and [Long-term memory](long-term-memory.md).
