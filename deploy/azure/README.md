# Hive BDC Azure Application offer

| File | What it is |
|---|---|
| `main.bicep` | The resources of one instance (web app, plan, storage account, Key Vault, sign-in identity, roles). Edit this file. |
| `mainTemplate.json` | The build of `main.bicep`; Partner Center reads this file. Rebuild after every change: `bicep build main.bicep --outfile mainTemplate.json`. |
| `createUiDefinition.json` | The installation wizard: sign-in client ID and plan size. |
| `bicepconfig.json` | Linter rules (recent API versions, no secrets in outputs, artifacts parameters). |
| `test-template.sh` | Bicep build and linter, arm-ttk, the sign-in script parse and `DeploymentTemplateTests`. |
| `package.sh` | Runs the tests, publishes the service with the update key and writes `dist/hivebdc-azure-{version}.zip`, plus the self-update package `dist/azure/hivebdc-app-{version}.zip` and its unsigned `dist/azure/latest.json`. It stops without `Hosting/update-key.pem` and without changelog notes for the version. `UPDATE_KEY_FILE` builds with a test key instead (files end in `-test`, never released); `UPDATE_FEED_URL` sets the address in `latest.json`. |
| `../../build/sign-azure-package.ps1` | On the signing host: checks that the package carries the fingerprint of the KMS update key, signs the package and the feed entry (text from `build/azure-update-statement.ps1`) with that key and writes both signatures into `latest.json` once they verify. |

Managed applications accept ARM language version 1.0 only, so `main.bicep` uses no Bicep
feature that needs 2.0 (the tests fail if the build has a `languageVersion`).

The package holds `mainTemplate.json`, `createUiDefinition.json` and `app/hivebdc.zip`.
During the installation Azure gives the template a link to the package that works only
while the deployment runs; the template copies `app/hivebdc.zip` into the app with the
`onedeploy` extension and the app runs it from `/home/data/SitePackages`.

The sign-in script is `scripts/New-HiveBDCSignInApp.ps1` (see the main README, "Installing in
Azure").