# Fanvue UI — Public Code Reference

A **read-only snapshot** of the Fanvue module's WPF UI layer, extracted from the Socinator project as a visual / code-pattern reference for design work.

> **NOT runnable.** Backend services (`FanvueDominatorCore`, `DominatorHouseCore`) and runtime dependencies are intentionally omitted. Anything outside this UI tree will be unresolved by design.

## What's Inside

```
Public Fanvue Extract/
|-- README.md                         <- this file
`-- FanvueDominatorUI/                <- WPF UI source only
    |-- App.xaml, App.xaml.cs
    |-- FanvueDominatorUI.csproj
    |-- Controls/Setup/               <- Setup wizard, accounts grid, analytics, debug controls
    |-- Factories/                    <- DI factories (account count/update, network collection, publisher)
    |-- FvCoreLibrary/                <- Tab handler, core builder, network core factory
    |-- IoC/                          <- Unity container extension, dominator module
    |-- Modules/                      <- Prism module entry
    |-- Properties/                   <- AssemblyInfo
    |-- TabManager/                   <- The six tabs (Setup, Account, Analytics, Engage, Scraper, Settings)
    |-- ViewModels/                   <- Settings + Engage view models (MVVM)
    `-- Views/                        <- Engage, Notifications, Settings sub-views
```

## Where the Designer Should Look

| You want to see... | Open... |
|---|---|
| Overall WPF visual layout | any `.xaml` file under `TabManager/`, `Controls/Setup/`, `Views/` |
| Setup / OAuth wizard UI | `Controls/Setup/FanvueSetupWizard.xaml` (+ `.xaml.cs`) |
| Accounts grid | `Controls/Setup/FanvueAccountsControl.xaml` |
| Revenue / analytics dashboard (LiveCharts) | `Controls/Setup/FanvueAnalyticsControl.xaml` |
| Tab structure | `TabManager/SetupTab.xaml`, `AccountTab.xaml`, `AnalyticsTab.xaml`, `EngageTab.xaml`, `ScraperTab.xaml`, `SettingsTab.xaml` |
| MVVM patterns used | anything under `ViewModels/` |
| DI / module wiring | `IoC/FvContainerExtension.cs`, `Modules/FanvuePrismModule.cs`, `FvCoreLibrary/FvTabHandlerFactory.cs` |

## Tab Layout (six tabs — note the unusual ordering)

| Index | Tab | Status in real product |
|---|---|---|
| 0 | Setup (OAuth config) | Complete |
| 1 | Accounts | Complete |
| 2 | Analytics (LiveCharts) | Complete |
| 3 | Engage | Template / placeholder |
| 4 | Scraper | Template / placeholder |
| 5 | Settings | Complete |

Setup is at index 0 here — every other Socinator module puts Accounts at index 0. Worth noting because some shared UI hooks (e.g., `UpdateAccountCustomControl`) target index 1 for Fanvue specifically.

## What Has Been Redacted

A scan was performed before publishing. Categories checked:

- `ClientId` / `ClientSecret` / `RedirectUri` — only field, property, and local-variable names appear. Zero hardcoded literal values in the source. The Fanvue module is engineered so credentials are entered at runtime through the Setup Wizard and persisted locally per-machine.
- Bearer / access tokens / refresh tokens — none present in source. Runtime values only.
- Local user paths (`C:\Users\...`, `D:\...`), real creator handles, internal repo identifiers, GitHub / OpenAI tokens — none present in this UI tree.

The strings `https://auth.fanvue.com`, `https://api.fanvue.com`, and `https://www.fanvue.com/developers/apps` are kept — they are Fanvue's public OAuth and developer-portal URLs. The single `Password = "oauth"` literal in account-creation code is a placeholder string indicating OAuth-mode authentication, not a real password.

## License & Use

This snapshot is provided **for design reference only**. The source code is proprietary and remains the property of its authors. **No license is granted** for redistribution, modification, or use in derivative works. Do not republish, fork, or incorporate this code into other projects without express written permission.

## Provenance

- Module name: Fanvue
- Source library: `fanvuedominator-library`
- Component: WPF UI only (`FanvueDominatorUI`)
- Snapshot date: 2026-04-27
