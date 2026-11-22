# Publishing HighPop to the Snap Store

The packaging lives in `snap/` and is built and smoke-tested by `.github/workflows/snap-test.yml`. Nothing is uploaded
automatically. These steps are the ones only the account owner can do.

## 1. Create the publisher account and register the name

1. Sign in at <https://snapcraft.io/account> with an Ubuntu One account and choose the **display name** `RogueAssassin`.
   The store shows this name as the publisher on the app page and in Ubuntu's App Center.
2. Accept the Snap Store developer terms.
3. Register the snap name (run on any machine with snapcraft installed, signed in with `snapcraft login`):

   ```bash
   snapcraft register highpop-rust-manager
   ```

   If the name is taken, pick another and change `name:` in `snap/snapcraft.yaml` (the app command and desktop file names
   follow it).

## 2. Request classic confinement

HighPop starts and watches game-server processes and manages files across the user's drives, which strict confinement
blocks. Classic confinement must be approved by the store reviewers before an upload is accepted.

1. Post a request in the **store-requests** category of the Snapcraft forum (<https://forum.snapcraft.io>), titled
   `Classic confinement request for highpop-rust-manager`.
2. Explain why: it launches and supervises Rust dedicated-server processes (and SteamCMD), reads and writes server
   folders the user chooses on any drive, and starts the user's own tools. Link the public source repository and mention
   the MIT licence.
3. Wait for a reviewer to approve it. The store may also ask for publisher vetting; see the Snapcraft documentation on
   reviewing classic confinement snaps.

## 3. Verified publisher (optional)

The verified checkmark is aimed at organisations. Request it later from the forum if you want it.

## 4. Upload

Once the name is registered and classic is approved:

```bash
snapcraft export-login --snaps highpop-rust-manager --channels stable --acls package_access,package_push,package_release credentials.txt
```

Store the file's contents as the repository secret `SNAPCRAFT_STORE_CREDENTIALS` and delete the file. Wiring the release
workflow to build and upload the snap is the next step after this; ask for it when the approval comes through.

## Testing locally without the store

```bash
sudo snap install --dangerous --classic ./highpop-rust-manager_*.snap
```

Updates for a store install come from snapd, which refreshes snaps in the background. The app's own updater stays off
inside a snap, and its update banner says so.
