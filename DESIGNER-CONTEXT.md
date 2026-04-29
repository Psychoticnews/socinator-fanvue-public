# Fanvue Analytics — Designer Context

This doc gives an external designer everything they need to design a new Fanvue Analytics dashboard that drops cleanly into the Socinator codebase. Combined with the `FanvueDominatorUI/` source in this repo and screenshots from the running app.

Last updated 2026-04-28.

---

## 1. What Socinator is

Windows desktop multi-network social-media automation suite. Manages many accounts at once across 8 platforms — Instagram, Twitter/X, Reddit, Pinterest, Snapchat, YouTube, Outlook (utility), and Fanvue (creator platform) — with browser automation, post scheduling, mass campaigns, content scrapers, and analytics. Power users run it to operate 10–100s of accounts.

The Fanvue module is one of those 8 modules. We're redesigning its **Analytics** sub-tab — not adding a new section.

---

## 2. The Analytics page — what it's for

**Who uses it:** Fanvue creators or agency operators managing 1–50+ creator accounts. Money is the primary metric; they're checking this page multiple times a day.

**Problem it solves:** Fanvue's native dashboard is single-account and surface-level. Socinator users need:
- Multi-account aggregation
- "How much today / this week / this month / this year / ever"
- Where the money comes from (subscriptions / PPV messages / tips / renewals / referrals)
- Top spenders and newest subs (priorities for personal outreach)
- Real-time-ish notifications (new message / sub / follower / tip / purchase)
- Trend awareness — month-over-month, sub growth/churn, day-of-week patterns

**Core jobs:**
1. Daily check-in — see today's deltas + live toasts
2. Trend analysis — switch range, spot peaks/dips
3. Source diagnosis — pie shows where revenue is shifting
4. Fan prioritization — Top Spenders / Newest Subs lists
5. Cross-account comparison — which account is hot
6. Export for accounting/tax — CSV/JSON

**What it is NOT:** posting tool, chat client, content manager. Those live in other tabs.

**Visual / emotional tone:** dark, data-dense but breathable, professional. Money users want signal, not decoration. Gold = money, green = growth, red = churn/error.

---

## 3. Tech stack constraints

| Item | Value |
|---|---|
| OS | Windows desktop only |
| Framework | WPF on .NET Framework 4.8 |
| Language | C# 7.2 (no records, no nullable refs, no top-level statements) |
| Markup | XAML |
| MVVM | **Prism 7.2.0.1422** (`Prism.Mvvm.BindableBase`, `Prism.Commands.DelegateCommand`) |
| DI container | **Unity 5.11.1** via `Prism.Unity.Wpf` |
| Theme chrome | **MahApps.Metro 1.6.5** |
| Charting | **LiveCharts.Wpf** is referenced and a shared `AccountGrowthChartControl` already exists. **Use it.** Don't propose hand-drawn Canvas shapes. |
| JSON | Newtonsoft.Json |

---

## 4. Theme + design tokens

The host shell owns the theme. Fanvue's `App.xaml` is empty — `Application.Resources` lives in `DominatorUIUtility\Themes\PrussianBlue.xaml` (host-side).

The current AnalyticsTab IGNORES the theme (almost all hex hard-coded). The redesign should switch to `DynamicResource` so the dashboard tracks the app's theming.

### Theme keys available (use these, not hard-coded hex)

```
Brushes (DynamicResource keys)
  AccentBaseColorBrush                  primary accent (Prussian Blue #065265)
  AccentColorBrush  / 1 / 2 / 3 / 4     accent variants
  DarkBlueBrush                         #023851
  TextColorBrushAccordingTheme          black on light, white on dark
  TextColorBrushAccordingTheme1         #233140
  IconFillBrushAccordingTheme           icon glyph fill
  ListItemsMouseHoverColorAccordingTheme  #D6EBF2 hover
  GreenColorAccordingTheme              positive money/growth
  TabBorderSolidBrush                   tab separators
  UserControlBackgroundBrush            card / panel surface
  LoggerListBackground                  #F2F2F0 row strips
  IdealForegroundColorBrush             contrasted foreground
  HighlightBrush                        emphasis/selection
  ProgressBrush                         LinearGradient (green → accent3)
  CheckmarkFill / RightArrowFill        small icon fills
  MetroDataGrid.* / MahApps.Metro.* ...  MahApps + datagrid skins
```

### Designer token block (use as a starting guide — actual app pulls from PrussianBlue.xaml at runtime)

```
Surfaces — dark theme
  BgBase           #1E1E1E   page bg
  BgPanel          #2D2D30   primary card surface
  BgSubPanel       #252528   nested panels, inputs
  BgRow            #2D2D30
  BgRowHover       #353538
  BgRowSelected    #3D3D40
  BgInput          #1E1E1E

Borders
  BorderSubtle     #3D3D40
  BorderNormal     #555555
  BorderAccent     #4CAF50

Text
  TextPrimary      #FFFFFF   currency totals, big numbers
  TextStrong       #E0E0E0   section titles, body emphasis
  TextSecondary    #B0B0B0   labels, sub-text
  TextMuted        #888888   meta, captions
  TextDisabled     #555555

Brand / accent
  Success / Active     #4CAF50  green — growth, online
  Info                 #2196F3  blue — neutral / Net toggle
  Warning              #FF9800  orange — rate limit, throttled
  Subscriber accent    #E91E63  pink
  Earnings highlight   #FFD700  gold — money totals
  Error / Danger       #F44336  red — failures, churn
  Outbound chat bubble #0078D4
  Inbound chat bubble  #3A3A3D

Pie / chart palette (10-color, distinct on dark)
  #4CAF50  #2196F3  #FF9800  #E91E63  #9C27B0
  #00BCD4  #FFEB3B  #F44336  #607D8B  #795548

Typography (system Segoe UI on Windows)
  Page title          14 SemiBold White
  Section title       14 SemiBold White
  Card label          11 SemiBold #888888 ALL CAPS letter-spacing 0.5px
  Big number (card)   28 Bold (accent color)
  Big number (hero)   36-40 Bold White
  Body                13 Regular #E0E0E0
  Sub-text            11-12 Regular #B0B0B0
  Tooltip / meta      10-11 Regular #888888

Corner radius
  Card / panel        8px
  Inner tile          6px
  Button              4px
  Toast / pill        12px

Spacing scale       4 / 8 / 12 / 16 / 24 px

Shadow              none — flat dark theme. Use border instead.
Motion              ≤150ms transitions only. No long storyboards.
Theme               dark only. Light/dark toggle in title bar is decorative.
```

### Existing shared styles

`DominatorUIUtility\Styles\BaseStyles.xaml` exposes:
- `ButtonStyle` — 200x40 button, `#EE6544` orange-ish, used by primary CTAs
- `AccentedSquareButtonStyle` — square accent button
- `CircleButton` — round icon button
- `btn_style`, `canceledit_style` — variants
- `datagridHeaderStyle`
- `MahApps.Metro.Styles.ToggleSwitch.Win10`

---

## 5. Existing reusable controls — USE THESE before designing fresh

### From `FanvueDominatorUI/Views/`
- `Notifications/NotificationToast.xaml` — chrome-less always-on-top toast Window. 360×80, dark shadow Border, `TitleText` + `BodyText`. Used for "new message / new sub" pop-ups. **Already supports clickable toasts** (third overload `Show(title, body, Action onClick)`).
- `Engage/ChatMonitorControl.xaml` — full MVVM example, 3-pane chat layout, brush-token palette, paper-plane Path icon, hover live-poll dot.
- `Engage/MassMessagesControl.xaml` — MVVM with `<UserControl.DataContext><vm:MassMessagesViewModel /></UserControl.DataContext>` pattern. Good template.

### From `DominatorUIUtility/CustomControl/` (host-side, available everywhere)
- **`AccountGrowthChartControl.xaml`** — LiveCharts.Wpf `CartesianChart` UserControl, themed. Has `SeriesCollection`, `AxisXLabels`, `AxisXTitle` dependency properties. **The redesigned charts SHOULD use this** instead of hand-drawing on Canvas.
- `Loader.xaml` — spinner / loading indicator
- `NotificationPopUp.xaml` — modal popup
- `CustomDialogWindow.xaml`, `CustomInputDialog.xaml` — generic dialogs
- `HeaderControl.xaml`, `FooterControl.xaml` — standard window chrome
- `Reports.xaml` — existing reports view (worth referencing for table/chart layout patterns)
- `AccountCustomControl.xaml`, `SelectAccountControl.xaml`, `SingleAccountControl.xaml` — reusable account-row UI
- `ChatImageContainer.xaml`, `ChatMediaContainer.xaml`, `MediaDownloader.xaml`
- `IconsNonShared.xaml` — `PathGeometry` keys for icons (use these instead of new SVG paths where matches exist)

### Converters — already in `DominatorHouseCore/Converters/` (~40 of them)

Most useful for the redesigned Analytics:

| Converter | Use case |
|---|---|
| `BooleanToVisibilityConverter` | Toggle banners, empty states, panels via XAML binding (instead of code-behind) |
| `IntToVisibilityConverter` | Hide a section when count = 0 |
| `ListCountVisiblityConverter` | Empty-state for Top Spenders / Newest Subs lists |
| `EpochToDateTimeConverter`, `IntToDateTimeConverter` | Format Fanvue timestamps |
| `BoolToValueConverter` | Generic bool → value swap (chevron ▼/▶, gross/net label) |
| `IsPositiveValueConvertor`, `PositiveValueConvertor` | Green-up / red-down arrow on % change |
| `AverageConverter` (multi-value) | "this period vs prior period" deltas |
| `BooleanToStringSelectConverter` | "Gross" / "Net" label flip |
| `VisibleIfEqualConverter` | Show panel only when string equals param (e.g., BottomList switcher) |
| `SocialNetworkToVisualBrushConverter`, `SocialNetworkToColorBrushConverter`, `SocialNetworkToLinkConverter` | Per-network icon/brand color (multi-account) |
| `WorkingStringToVisibilityConverter` | "Loading..." indicator while a status string is set |
| `ImageConverter` | Bind avatar URLs to ImageSource |

**Designer takeaway:** the redesign can move ~all of Analytics's `if/else Visibility` code from code-behind into XAML using these. Combined with a `BindableBase` ViewModel, it eliminates roughly half of the current 4192-line code-behind.

---

## 6. Naming conventions

Across all 8 modules:

**Folder layout per module:**
```
<Network>dominator-library/
├── <Network>DominatorUI/
│   ├── TabManager/             top-level tabs (AccountTab.xaml, AnalyticsTab.xaml, ...)
│   ├── Views/<Feature>/         sub-views (Engage/ChatMonitorControl.xaml, ...)
│   ├── ViewModels/<Feature>/    paired ViewModels
│   ├── Controls/                custom controls
│   ├── IoC/                     DI registration (FvContainerExtension.cs)
│   ├── Modules/                 Prism module (FanvuePrismModule.cs)
│   ├── Factories/
│   └── Resources/
└── <Network>DominatorCore/      services, DTOs, models
    ├── Services/
    ├── Models/Dtos/
    └── ...
```

**x:Name conventions** (Pascal-case):
- `Cmb*` ComboBox
- `Btn*` Button
- `Txt*` TextBlock or TextBox
- `Border*` Border
- `Toggle*` ToggleButton
- `Lst*` ListView / ItemsControl

(Some legacy snake_case `lst_*` / `btn_*` exists in older code — don't propagate.)

**Module prefixes** (for finding code via grep):
- Fanvue → `Fv` / `Fanvue`
- Instagram → `Gram` / `Gd` / `GD`
- Twitter → `Twt` / `Td` / `TD`
- Reddit → `Rd` / `Reddit`
- Pinterest → `Pd` / `Pin`

---

## 7. MVVM pattern in this codebase

ViewModels derive from `Prism.Mvvm.BindableBase`. Commands are `DelegateCommand` / `DelegateCommand<T>`.

```csharp
using BindableBase = Prism.Mvvm.BindableBase;
using Prism.Commands;

public class FanvueAnalyticsViewModel : BindableBase
{
    private string _selectedAccountKey;
    public string SelectedAccountKey
    {
        get { return _selectedAccountKey; }
        set { SetProperty(ref _selectedAccountKey, value); }
    }

    public DelegateCommand RefreshCommand { get; }

    public FanvueAnalyticsViewModel()
    {
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), CanRefresh);
    }
}
```

**View ↔ VM wiring** (the pattern other Fanvue tabs already use):
```xml
<UserControl.DataContext>
    <vm:FanvueAnalyticsViewModel />
</UserControl.DataContext>
```

**Cross-module account access** uses service locator pattern:
```csharp
var accountVm = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
var fanvueAccounts = accountVm.AccountList
    .Where(a => a.SocialNetwork == SocialNetworks.Fanvue);
```

**DI registration** (host shell calls Fanvue's container extension at startup):
```csharp
// IoC/FvContainerExtension.cs
Container.RegisterType<ISocialNetworkModule, FvDominatorModule>("Fanvue");
Container.RegisterType<INetworkCollectionFactory, FanvueNetworkCollectionFactory>("Fanvue");
Container.RegisterType<IPublisherCollectionFactory, FanvuePublisherCollectionFactory>("Fanvue");
```

**Current Analytics is the outlier** — the only Fanvue tab with no ViewModel and ~4200 lines of code-behind. The redesign should be MVVM like the rest (MassMessagesControl / ChatMonitorControl / VaultControl / ChatterManagerControl).

---

## 8. Async / threading patterns

```csharp
private CancellationTokenSource _cts;
private async Task RefreshAsync()
{
    _cts?.Cancel();
    _cts = new CancellationTokenSource();
    try
    {
        IsLoading = true;
        foreach (var acc in _accounts)
        {
            await FetchAccountDataAsync(acc, _cts.Token);
        }
        UpdateUi();
    }
    catch (OperationCanceledException) { /* expected */ }
    catch (Exception ex) { ShowApiErrorBanner(ex.Message); }
    finally { IsLoading = false; }
}
```

- Sequential per-account fetches inside the refresh loop (lets the loading indicator show "i / N" progress).
- Within an account, the API calls are also sequential currently — `Task.WhenAll` is fine if the designer's mockup needs faster refreshes, just be aware of rate-limit budget (100 req / 60s per token).
- `DispatcherTimer` for any UI-thread work (toast auto-dismiss, snapshot persistence at 60s dwell).
- `CancellationTokenSource` per-refresh, cancelled if the user re-clicks Refresh.

---

## 9. Fanvue API surface — what data is available

Base URL: `https://api.fanvue.com`. Auth: OAuth2 Bearer token. Header: `X-Fanvue-API-Version: 2025-06-26`.

| Endpoint | Returns | Used for |
|---|---|---|
| `GET /users/me` | `fanCounts.{followersCount, subscribersCount}`, profile | Followers + Subscribers cards |
| `GET /insights/earnings/summary?startDate=…&endDate=…&granularity=day\|week` | `totals.{allTime, thisMonth}.{gross, net}`, `totals.thisMonth.previousMonthGross`, `totals.thisMonth.grossChangePercentage`, `overTime[].{periodStart, gross, net}`, `breakdownBySource{subs, messages, tips, renewals, referrals, posts}.{gross, net}`, `earningsByType{...}.{gross, net}` | Bar/line/area chart, totals cards, pie chart |
| `GET /insights/subscribers?startDate=…&endDate=…&size=50` | `data[].{date, newSubscribersCount, cancelledSubscribersCount, total}` | Subscriber gained/cancelled per day |
| `GET /insights/top-spenders?page=…&size=10` | `data[].{user.{uuid, handle, displayName, avatarUrl}, gross, net, messages}` | Top Spenders panel |
| `GET /insights/spending?cursor=…` | Cursor-based feed of spending events | Recent spending |
| `GET /chats?sortBy=most_recent_messages&size=20` | Recent chat threads with `lastMessage.{text, sentAt, sender}` | Live message detection |
| `GET /chats/unread` | `{unreadChatsCount, unreadMessagesCount, unreadNotificationsCount}` | Live notification dot |

### Hard constraints

1. **Currency in CENTS.** Divide by 100 for display. Always show `$X,XXX.XX`.
2. **Granularity** API only accepts `day` or `week`. `month` returns 400. So a multi-month range = weekly buckets.
3. **Hourly granularity NOT supported.** "Today" range = 1 day = 1 bucket — chart looks empty. Designer must design a special sub-day layout (big "Today so far" hero + 24h sparkline) instead of the standard chart.
4. **Subscriber insights capped at 365 days, max 50 events per request.** Long ranges show truncation note.
5. **Multi-year ranges 500 on Fanvue's server** for `/insights/earnings/summary`. Client chunks by year and merges.
6. **Rate limit: 100 req / 60s per token.** Each response carries `X-RateLimit-Remaining`. Polling cadence (10s active, 60s idle) adapts to keep budget below 20%.
7. **Top Spenders has no date-range param.** Always all-time. Label the panel as such.
8. **Pie breakdown is range-scoped** — driven by `breakdownBySource` / `earningsByType` from the earnings summary call.
9. **OAuth tokens rotate** — refresh token is single-use, regenerated each call. Persisted automatically.
10. **No webhooks** (yet). Polling only, ~10-60s latency.

### Webhooks (future)

Fanvue does have HTTP webhooks (HMAC-SHA256, 6 events: `message.received`, `message.read`, `follow.new`, `subscription.new`, `purchase.new`, `tip.new`) but they require a public HTTPS endpoint (Fanvue posts to a URL, not a desktop process). Out of scope for this redesign — we're staying on polling.

---

## 10. API client (`FanvueApiClient.cs`)

Single `HttpClient`-based class wrapping `https://api.fanvue.com`. Constants: `MaxRetries = 3`, `RateLimitWaitMs = 1000`, `Timeout = 30s`.

**Per-request method:** `private async Task<ApiResponse<T>> SendRequestAsync<T>(method, endpoint, body, token)` — adds `Authorization: Bearer <accessToken>` and `X-Fanvue-API-Version: 2025-06-26`.

**Public surface:** `GetAsync<T>` / `PostAsync<T>` / `PatchAsync<T>` / `DeleteAsync<T>` plus domain methods `GetCurrentUserAsync`, `GetChatsAsync`, `GetFollowersAsync`, `GetSubscribersAsync`, `GetMediaAsync`, `GetEarningsSummaryAsync`, `GetSubscriberInsightsAsync`.

**Auth + retry built in:**
- Pre-flight token refresh if `Credentials.IsTokenExpired && CanRefresh`.
- 401 → one refresh-and-retry, then `Failure("Unauthorized. Please reconnect your account.")`.
- 429 → respects `Retry-After` header, raises `ApiError` event, awaits + retries (up to `MaxRetries`).
- 5xx → linear-backoff retry.

**`ApiResponse<T>` shape:**
```csharp
public class ApiResponse<T>
{
    public bool IsSuccess { get; set; }
    public T Data { get; set; }
    public string ErrorMessage { get; set; }
    public int StatusCode { get; set; }
    public int RateLimitLimit { get; set; }
    public int RateLimitRemaining { get; set; }
    public int RetryAfterSeconds { get; set; }
}
```

Always check `resp.IsSuccess`; surface `resp.ErrorMessage` to UI through the API error banner pattern.

---

## 11. Existing data flow (today's wired-up reality)

```
AnalyticsTab_Loaded
    └── LoadAnalyticsCache (instant paint from analytics-cache.json)
    └── LoadAccounts() — read FanvueCredentials from accounts
        └── for each account:
            └── RefreshData()
                └── FetchAccountData(account):
                    ├── apiClient.GetEarningsSummaryAsync(start, end, granularity)
                    ├── apiClient.GetSubscriberInsightsAsync(start, end, 50)
                    ├── apiClient.GetEarningsSummaryAsync(last24Start, last24End, "day") [24h tile]
                    └── apiClient.GetSubscriberInsightsAsync(last24Start, last24End) [24h tile]
                └── aggregate dictionaries (_monthlyEarnings, _breakdownBySource, etc.)
            └── UpdateUI() — set every Txt*.Text
            └── DrawCharts()
                ├── DrawBarChart — paint Rectangle/Polyline/Polygon onto EarningsChart Canvas
                └── DrawPieChart — paint slices onto PieChart Canvas
            └── SaveAnalyticsCache (persist for next tab open)
```

Cache lives at `%LocalAppData%\Socinator1.0\Fanvue\analytics-cache.json`. Snapshots at `…\analytics-snapshots-history.json`. Section state at `…\analytics-section-state.json`. Last-seen at `…\analytics-last-seen.json`.

---

## 12. Sections / structure of the current dashboard

| # | Section | What it shows |
|---|---|---|
| 0 | API error banner | Red dismissible bar (V.4) |
| 1 | 4 stat cards (UniformGrid) | Followers / Subscribers / This Month / Total Earnings |
| 2 | 3 "Last 24h" tiles | Earnings 24h / Subs gained 24h / Followers gained 24h |
| 3 | Growth Tracker (collapsible) | Window dropdown + 2 tiles (Followers Gained / Subscribers Gained) |
| 4 | Earnings chart (collapsible) | Bar / Line / Area, single-series mutex toggles, Y-axis gridlines, hover tooltips, X-axis density-aware labels |
| 4 | Revenue Breakdown pie (collapsible) | By Source / By Type + Gross / Net sub-toggle, dynamic legend |
| 5 | Account Breakdown (multi-account view) | Per-account row table |
| 6 | Recent Spending (single-account view) | Per-event row table |
| 7 | Bottom switcher | ComboBox toggles between Top Spenders ↔ Newest Subscribers |
| Footer | Activity log strip (host-shared, persistent across all modules) | Don't redesign — just leave space |

Header bar (always visible at top): Account selector / Time Range dropdown / Export / Cache status / Refresh.

---

## 13. Time range dropdown — exactly 5 options

Default = **Today**.

| Label | Range |
|---|---|
| Today | midnight UTC → now |
| Last 7 Days | now − 7d → now |
| This Month | 1st of current month → now |
| This Year | Jan 1 of current year → now |
| All Time | 2020-01-01 → now (chunked into 365-day windows client-side) |

NO "This Week", "Last 30 Days", "Last 90 Days", "Last 6 Months", or "This Quarter" — those have all been removed.

---

## 14. Designer's mandate

Things to KEEP:
- Information architecture (the sections above)
- Single-series mutex toggles for the earnings chart
- Pie chart's two toggle dimensions (mode + value)
- Section collapsibility, with state persistence
- Cache hydration on tab open + "Loaded from cache · Updating…" indicator
- API error banner pattern (red, dismissible, top-of-page)
- Activity log strip at bottom (don't redesign)
- Cents → dollars formatting, UTC → local time

Things to FIX:
- Inconsistent text colors (5+ grays without rule)
- Inconsistent padding/typography across cards
- Side-by-side chart layout that squishes the pie at <1400px
- Empty states that read as broken (donut with "No earnings" inside)
- Today-range producing an empty single-bucket chart (needs special sub-day layout)
- Confusing time-range scope (some metrics scoped, some lifetime, no labels)
- Stale-vs-fresh data ambiguity
- Hand-drawn Canvas charts (replace with `AccountGrowthChartControl` / LiveCharts)
- ~4200-line code-behind (move to MVVM with `BindableBase` VM + `DelegateCommand`)
- Hard-coded hex everywhere (use theme `DynamicResource` keys)

Things to ADD:
- Live-poll status indicator (green/orange/gray dot + tooltip showing last poll + interval)
- Better data freshness pill ("Updated 2m ago")
- Heatmap calendar variant for All Time (variant B per intake)
- Money-first hero variant (variant B per intake)
- Toast notifications for live events (new message / sub / follower) — designer should design these in same mock since they're sibling UI

Things to AVOID:
- Custom `ControlTemplate` with `Triggers` — historically crashes XamlParseException
- `Color="{Binding}"` on shapes — must bind to `Brush` properties
- Emoji or icon fonts — use `Path` geometry only
- Light theme — entire app is dark only
- Drag-and-drop within same control
- Animations longer than 150ms
- Third-party UI libs not already referenced (no Material Design In XAML, no Avalonia, no MAUI)

---

## 15. Critical lessons from the previous iterations

We iterated on this dashboard ~20 times. Designer should treat these as landmines:

1. **Multi-series chart collapse.** Don't propose mixing dollars + counts on the same Y-axis.
2. **Value labels on every dot destroy readability.** Default to NO permanent labels on dense data — hover tooltips + Y-axis gridlines + first/last/extrema labels.
3. **Time range scopes some metrics, not others.** Visually segregate or label every metric with its scope.
4. **API rejects month granularity.** No monthly bar charts.
5. **Daily events vs weekly buckets** — designer doesn't see this directly, but plan a footer like "50 events grouped into 27 weekly buckets" so missing data is detectable.
6. **Stale-data on fetch failure** — every panel needs a unified "showing last-known-good data" state.
7. **Toggle UI got crowded** — separate global controls (account/range/refresh/export) from chart-scoped (series/type) into different rows.
8. **Pie chart squished side-by-side with bar chart** — design layout that reflows or stacks vertically, not fixed two-column.
9. **Empty states looked broken not empty** — design intentional empty illustrations with suggested actions.
10. **Data freshness invisible** — "Updated h:mm tt" too quiet. Make it a first-class element.
11. **Chart fights the data resolution at All Time** — heatmap calendar variant for >1 year ranges.
12. **Status indicators got missed** — design ONE coherent status zone (probably top-right) that combines connection state · last-poll time · cache state · errors.
13. **Accessibility** — pass WCAG-AA contrast (4.5:1 body, 3:1 large). Sub-text not lighter than `#A0A0A0` on dark.
14. **Defaults** matter — pick a default range that produces a useful, populated chart.
15. **Number formatting inconsistent** — define ONE money format rule and apply everywhere.
16. **Unprofessional gray hierarchy** — exactly 3 text colors and 3 weights, document what each means.
17. **Change calculation polarity** — color "good" / "bad" PER METRIC (subscriber churn red even though value is positive count). Empty state when comparison can't be computed (previous period $0 → "—" not `NaN%`).
18. **Big-number cards lacked rhythm** — define one Stat Card component with strict slots: Label / Big Number / Delta Indicator / Comparison Anchor.

---

## 16. Pointers to source in this repo

The `FanvueDominatorUI/` folder in this repo is the actual UI source. Designer can read:
- `TabManager/AnalyticsTab.xaml` — current dashboard layout (hard-coded hex, 8-section ScrollViewer)
- `TabManager/AnalyticsTab.xaml.cs` — current behavior (4192 lines, code-behind only, no VM)
- `Views/Engage/MassMessagesControl.xaml` + paired ViewModel — example of how MVVM is done correctly
- `Views/Engage/ChatMonitorControl.xaml` — 3-pane chat view with brush tokens, live dot, toast wiring
- `Views/Notifications/NotificationToast.xaml` — toast popup spec (reuse for new-message / new-sub toasts)

(The shared `DominatorUIUtility/Themes/PrussianBlue.xaml`, `DominatorUIUtility/Styles/BaseStyles.xaml`, `DominatorUIUtility/CustomControl/AccountGrowthChartControl.xaml`, and `DominatorHouseCore/Converters/*.cs` are NOT in this public extract. Their structure is documented in §4, §5, §7 above.)

---

## 17. Variations to produce

Per design intake confirmation:

**Variation A — "polished current structure"**: same information architecture, but proper typography, unified card component, designed empty/loading/error states, fixed color hierarchy, properly scoped time-range visual language.

**Variation B — "rethought IA, money-first"**: hero tile dominates with money headline + delta. Secondary stats demoted to a strip. Chart and Revenue Breakdown given full-width treatment. Today-default sub-day layout lives here (big "Today so far" hero + 24h sparkline). All-Time uses calendar heatmap.

Both fully interactive on the design canvas, with hover/empty/error/loading states represented.

For each component, deliver:
1. Exact hex colors (no rgba/oklch — we'll plug them as static brushes)
2. Pixel measurements (4 / 8 / 12 / 16 / 24 px scale)
3. State variants: default / hover / active / disabled / error
4. Iconography as SVG path data (the `d="..."` string)
5. Empty-state designs for every panel that can be empty
6. Layout grid showing reflow at 1280px → 1920px (no mobile / responsive)

---

That's everything. Combined with the source in this repo + screenshots from the running app, the designer has the full picture.
