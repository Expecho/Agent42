# PWA install & sign-in

> Install Agent42 as an app, and sign in with single sign-on.

## Install as an app

Agent42 is a progressive web app. Installing it gives you its own window and a home-screen icon.

- **Android / Chrome:** open Agent42 in the browser and choose **Install app** (or **Add to Home screen**).
- **Desktop Chrome or Edge:** use the install icon in the address bar.

Installing is required for [push notifications](push-notifications.md) and the
[Android Share sheet](android-share-target.md). The app keeps a copy of its interface so it opens
quickly. When you are offline you can still open it, but chatting needs a connection. New versions
are picked up automatically.

## Sign in

On the hosted deployment you sign in with your Microsoft (Entra ID) account. Access is invite-only:
you need to be invited and assigned by the person running the instance, and guests from other
organisations can be invited too. Your sign-in decides which conversations and settings are yours.
Other users cannot see them.

If you run it locally in the offline dev setup, or self-host without authentication, there is no
sign-in and everything belongs to one local user.

## Using a second account

An operator can link a second Microsoft account to your data so both sign-ins show the same
conversations. This gives that account full access to yours, so it is only meant for your own
accounts. Nothing is copied or moved.

## Sessions

Your sign-in session lasts a while and renews itself. If you are sent back to the Microsoft sign-in
page, just sign in again.

## For operators

- Sign-in is Entra ID through the platform's built-in authentication in front of the app.
  Turn on "assignment required" on the enterprise application so only invited people can get in.
- Outside Azure Container Apps the app can do the same sign-in itself (`Auth__Mode=entra`). The session is the same cookie,
  `AppServiceAuthSession`, valid for 14 days from sign-in in both modes, and the Android app reads it the same way. Signing
  out ends your Entra session too. See [Docker with Entra sign-in](../setup/docker-entra.md).
- Some people (owners, listed in the `Access:Owners` setting) also see the diagnostics pages. See
  [Usage & diagnostics](usage-and-diagnostics.md) and [Configuration health](configuration-health.md).
- Setup is covered in [Azure deployment](azure-deployment.md).
