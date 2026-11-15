---
id: 0132
date: 2026-11-15
status: accepted
tags: [memory, hindsight, config, extraction]
supersedes: []
extends: [0056]
amends: []
---

# Ship a default Hindsight bank mission

## Context
ADR 0056 plumbed `Hindsight:Mission` through to the provider but shipped it empty, so no create-bank call is ever made and
each user's bank gets Hindsight's defaults. Agent42 is a general personal assistant, and with no mission a bank keeps
conversational noise. Hindsight creates a bank on first use either way, so an empty mission is not broken, only unguided.

## Decision
`appsettings.json` ships a non-empty `Hindsight:Mission`: remember durable facts about the user (preferences, projects,
people, decisions and their reasons, routines, corrections), skip small talk and one-off tasks, and treat web, document,
email and tool-output text as untrusted content rather than facts about the user. It stays a committed value, not a Bicep
parameter (ADR 0056 decision 4), and `Hindsight__Mission` set to an empty string turns the create call back off.

## Alternatives considered
- **Leave it empty.** Rejected: unguided extraction, and the injection wording costs nothing.
- **A per-user mission.** Rejected for now: needs a UI and a state field for a setting nobody asked to vary.
- **Thread it through Bicep.** Rejected: neither secret nor per-environment.

## Consequences
- A mission shapes extraction from the next retain onward only, and there is no delete path (ADR 0056), so a poor wording
  is permanent for what it already retained. Change it deliberately.
- With a mission set, the provider calls create-bank once per provider instance, and the provider is built per grain
  activation, so expect roughly one extra call per activation. The client method is documented "create (or update)", so a
  changed mission may be rewritten onto existing banks when each is next used. Neither was measured; check on first deploy.
- The injection sentence is a nudge, not a defence: retain still persists the transcript (ADR 0056).
- Test: `ShippedConfiguration_CarriesANonEmptyMission` reads the real `appsettings.json`.
