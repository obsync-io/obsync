# Obsync Design System

**Date:** 2026-07-16 · **Documents:** `main` @ `a917680` (v0.8.3)

This document describes the design system **as it exists** in the WPF app — the actual token names, values, styles, and rules in `src/Obsync.App/Themes/` and `App.xaml`. It is documentation, not a redesign proposal. The system is enforced by `tests/Obsync.App.Tests/DesignSystemTests.cs`, which loads the real `App` resource graph, asserts the key resources resolve, and measures/arranges every view so template errors fail the build.

**Where things live:**

| File | Contents |
|---|---|
| `Themes/Colors.xaml` | Palette (colors + brushes), corner radii, spacing tokens |
| `Themes/Typography.xaml` | Font families, text styles |
| `Themes/Icons.xaml` | Icon font, glyph resources, base `Icon` style |
| `Themes/Controls.xaml` | Surfaces, buttons, inputs, DataGrid, tabs, scrollbars |
| `Themes/BrandLogo.xaml` | `ObsyncWordmark` vector (brand ink `#201E1F`) |
| `Themes/Theme.xaml` | Merges the above, in dependency order |
| `App.xaml` | Converters, badge/chip `DataTemplate`s, VM→View mappings |

The design intent throughout is **calm enterprise**: flat surfaces, hairline borders, one accent color, low-saturation status tints, no gradients, no shadows on content.

---

## 1. Color palette

All colors are defined once in `Colors.xaml` as `Color` resources with matching `SolidColorBrush` resources (`XxxColor` / `XxxBrush`). Screens reference brushes, never hex values.

### Surfaces and chrome

| Token | Value | Use |
|---|---|---|
| `AppBackgroundColor` | `#F8FAFC` | Window/page background |
| `CardColor` | `#FFFFFF` | Cards, inputs, nav rail, DataGrid background |
| `PanelColor` | `#F1F5F9` | Inset panels, grid headers, hover fills, disabled input fill |
| `BorderColor` | `#E5E7EB` | All hairline borders, dividers, grid lines |
| `RowHoverColor` | `#F8FAFC` | DataGrid row hover |

### Text

| Token | Value | Use |
|---|---|---|
| `TextPrimaryColor` | `#111827` | Primary ink |
| `TextMutedColor` | `#6B7280` | Secondary text, captions, icons at rest |

### Brand accent

| Token | Value | Use |
|---|---|---|
| `AccentColor` | `#1B17FF` | Brand blue (matches the logo/icon): primary buttons, links, focus rings, selected nav/tab, checked checkbox |
| `AccentHoverColor` | `#1512D6` | Primary button hover, link hover |
| `AccentSoftColor` | `#EBEAFF` | Selected rows, highlighted combo items, accent badges |

The wordmark itself uses brand ink `#201E1F` (`BrandLogo.xaml`) — that color is part of the logo asset, not a UI token.

### Status colors + soft tints

Every status color has a **soft tint** pair used as badge/banner background, with the strong color used for the dot and text on top of it:

| Strong | Value | Soft | Value |
|---|---|---|---|
| `SuccessColor` | `#16A34A` | `SuccessSoftColor` | `#E7F5EC` |
| `WarningColor` | `#D97706` | `WarningSoftColor` | `#FBF0E1` |
| `ErrorColor` | `#DC2626` | `ErrorSoftColor` | `#FBE9E9` |
| (accent) | `#1B17FF` | `AccentSoftColor` | `#EBEAFF` |
| (neutral) | — | `NeutralSoftColor` | `#F1F5F9` |

### Diff emphasis

Word-level intra-line diff highlights, deliberately stronger than the soft line tints so changes pop inside a changed line:

| Token | Value |
|---|---|
| `DiffAddedEmphasisColor` | `#BFE8CD` |
| `DiffDeletedEmphasisColor` | `#F5C8C8` |

### Scrollbars

`ScrollThumbBrush` `#C9CED6` at rest, `ScrollThumbActiveBrush` `#A8AFBA` on hover/drag.

**Rule: no new colors.** Every hue in the app comes from this file. The single deliberate exception in `Controls.xaml` is the `DangerButton` hover `#B91C1C` (a darker step of `ErrorColor`).

---

## 2. Spacing & radii tokens

Defined in `Colors.xaml`:

| Token | Value | Use |
|---|---|---|
| `RadiusControl` | `6` | Buttons, inputs, panels, banners |
| `RadiusCard` | `10` | Cards, toasts |
| `RadiusPill` | `12` | Status badges, tag chips |
| `PagePadding` | `28,24` | Page root margin (views use `Margin="28,24"`) |
| `CardPadding` | `20` | Default `Card` padding |

Common non-tokenized conventions seen across views: `12` padding for panels, `14,10` for banners, `16,8` / `14,8` / `10,6` button padding by emphasis, `44` minimum DataGrid row height.

---

## 3. Typography

`Typography.xaml`. Two font families:

- `AppFontFamily` = `Segoe UI Variable Text, Segoe UI` — everything.
- `MonoFontFamily` = `Cascadia Mono, Cascadia Code, Consolas` — code, diffs, line numbers, commit SHAs.

Text styles (all `TargetType="TextBlock"`):

| Style | Size / weight | When to use |
|---|---|---|
| `PageTitle` | 22 SemiBold, `TextFormattingMode=Display` | One per page, in the header |
| `SectionTitle` | 15 SemiBold | Card headings, dialog titles, empty-state headlines |
| `MetricValue` | 28 SemiBold, Display mode | Dashboard metric numbers only |
| `Body` | 13, wraps | Default prose |
| `BodyStrong` | 13 SemiBold (based on `Body`) | Primary cell text (e.g. job name), toast titles |
| `Muted` | 13, `TextMutedBrush` (based on `Body`) | Secondary prose, page subtitles, descriptions |
| `Caption` | 12, `TextMutedBrush` | Field labels (uppercase by convention, e.g. `SERVER NAME`), hints, timestamps |

An **implicit** `TextBlock` style sets `AppFontFamily`/13 only. It deliberately does **not** set `Foreground`: the default ink is inherited from the Window, so a bare `TextBlock` inside a colored control (e.g. a white-on-accent `PrimaryButton` label) inherits the button's foreground instead of being forced dark. Do not "fix" this by adding a Foreground setter.

---

## 4. Icons

`Icons.xaml`. Glyphs come from the system icon font — `IconFontFamily` = `Segoe Fluent Icons, Segoe MDL2 Assets` (Windows 11 with Windows 10 fallback). No icon images, no third-party icon packs.

Every glyph is a **named string resource** so screens never embed magic characters: `IconDashboard`, `IconJobs`, `IconConnections`, `IconRepositories`, `IconHistory`, `IconSettings`, `IconRun`, `IconEdit`, `IconAdd`, `IconDelete`, `IconBack`, `IconRefresh`, `IconUpdate`, `IconOpenExternal`, `IconExport`, `IconImport`, `IconClose`, `IconCheck`, `IconWarning`, `IconError`, `IconFolder`, `IconDatabase`, `IconBranch`, `IconOpen`, `IconDiff`, `IconCopy`, `IconChevronDown`, `IconChevronUp`, `IconNavToggle`.

Usage: a `TextBlock` with `Style="{StaticResource Icon}"` and `Text="{StaticResource IconXxx}"`. The `Icon` style sets the icon font, 16px, `TextMutedBrush`, centered, ClearType rendering. Override `FontSize`/`Foreground` locally where needed (14 inside buttons, 20–24 in dialogs/empty states; `Foreground="White"` inside a `PrimaryButton`).

Adding a glyph = add one `sys:String` to `Icons.xaml` with a code point from the Segoe Fluent set.

### Brand

- `ObsyncWordmark` (`BrandLogo.xaml`) — the logotype as a `DrawingImage` (vector outlines, ink `#201E1F`), rendered via `Image` so it scales crisply. Used in the nav rail.
- `Assets/Obsync_Icon.png` — the icon mark; window icons and the rail brand block. Its pack URI is verified by a test (`BrandIcon_ResourceLoadsFromPack`).

---

## 5. Buttons

All in `Controls.xaml`, all keyed (no implicit Button style). All show `Cursor=Hand`, use `RadiusControl` corners, and dim to `Opacity 0.5` (0.4 for `IconButton`) when disabled.

| Style | Look | When to use |
|---|---|---|
| `PrimaryButton` | Accent fill, white SemiBold text, `AccentHoverBrush` hover, padding `16,8` | The one main action of a page or dialog (Create Sync Job, Save, OK). At most one per visual scope |
| `DangerButton` | `ErrorBrush` fill, white text, `#B91C1C` hover | Destructive confirmations (Delete). Pair with a confirm dialog |
| `SecondaryButton` | Card fill, hairline border, `PanelBrush` hover, padding `14,8` | Alternate actions next to a primary (Import, Export, Test) |
| `SubtleButton` | Transparent, accent text, `PanelBrush` hover, padding `10,6` | Low-emphasis actions: Cancel in dialogs, row-adjacent actions |
| `IconButton` | 32×32 glyph-only, transparent, `PanelBrush` hover + accent glyph on hover | Table row actions, toast dismiss, nav collapse. Content is an icon `TextBlock`. **Must** carry `ToolTip` and `AutomationProperties.Name` (see §14) |
| `LinkButton` | Text-only accent hyperlink, underline on hover | Inline links: commit SHA, "Open on GitHub", toast actions |
| `NavButton` | `RadioButton` style: 3px accent left indicator + `PanelBrush` fill + accent SemiBold text when checked | Navigation rail items only |

Buttons with an icon + label compose a horizontal `StackPanel`: icon `TextBlock` (`Icon` style, FontSize 14) + label with `Margin="8,0,0,0"`.

---

## 6. Inputs

Implicit styles in `Controls.xaml` — every input picks these up automatically.

- **TextBox / PasswordBox** — card background, hairline border, `RadiusControl` corners, padding `10,7`, vertically centered content. **Focus ring:** `IsKeyboardFocusWithin` swaps the border to `AccentBrush` (the focus indicator app-wide — no glow, no reboxing). Disabled = `PanelBrush` fill.
- **ComboBox** — 36px tall, same box treatment, custom stroked chevron (`TextMutedBrush`), accent border on hover *and* focus; popup is a rounded (8px) card with 4px inset items. `ComboBoxItem` hover = `PanelBrush`; highlighted/selected = `AccentSoftBrush` fill + accent text.
- **CheckBox** — custom 18×18 box, 4px radius; checked = accent fill + white `IconCheck` glyph; hover = accent border. Label sits 8px right.

Field layout convention (see `AddServerWindow.xaml`): `Caption`-styled uppercase label, the input with `Margin="0,4,0,16"`, and an optional `Caption` hint line directly under the input.

---

## 7. DataGrid conventions

The implicit `DataGrid` style bakes in the product decisions — a grid dropped into a page is correct by default:

- **Read-only, single full-row selection:** `IsReadOnly`, `CanUserAddRows/DeleteRows/ReorderColumns = False`, `SelectionMode=Single`, `SelectionUnit=FullRow`, `AutoGenerateColumns=False`.
- **No horizontal scrolling — ever:** `ScrollViewer.HorizontalScrollBarVisibility=Disabled` is set in the base style (deliberately, after clipped-action-button bugs). Columns must fit the viewport:
  - content columns: star-sized (`Width="1.7*"` etc.) with a `MinWidth`, cell text `TextTrimming="CharacterEllipsis"`;
  - action/status columns: **fixed** width so trailing buttons can never be pushed out of view.
- **Visuals:** column headers are `PanelBrush` / muted 12px SemiBold / 40px tall; rows are transparent, `MinHeight=44`, horizontal hairline grid lines only; hover = `RowHoverBrush`, selected = `AccentSoftBrush`. Row virtualization on.
- Grids live inside a `Card` border with `Padding="0"` so the header meets the card edge.

---

## 8. Tabs

Underline tabs (Job Workspace, Settings, History Runs/Timeline). `TabControl` is chromeless: a hairline bottom border under the `TabPanel`, content 16px below. `TabItem` is muted text with a transparent 2px bottom border; selected = accent underline + accent SemiBold text; hover = primary ink.

Accessibility note baked into the template: the content presenter is named `PART_SelectedContentHost` — without that exact name the selected tab's content is absent from the UI Automation tree and screen readers only see the headers. Keep it if the template is ever touched.

---

## 9. Cards, panels, dividers

- `Card` — white, hairline border, `RadiusCard`, `CardPadding`, `SnapsToDevicePixels`. **Flat by design:** a hairline border instead of a drop shadow (see §15 for why).
- `Panel` — `PanelBrush` inset, hairline border, `RadiusControl`, 12px padding. Sub-areas, checklists, callouts, empty-state icon chips.
- `Divider` — 1px `BorderBrush` rule with `Margin="0,12"`.

---

## 10. Status badges, tag chips

Reusable `DataTemplate`s in `App.xaml`, driven by converters (also in `App.xaml`).

**The never-color-only rule:** a status is always communicated by **a dot (or glyph) plus text**, never by color alone — badges pair an `Ellipse` with a label, banners pair an icon with text. Color-blind users and grayscale screenshots must still read correctly.

- **`StatusBadgeTemplate`** (DataContext = `RunStatus`, or null): pill (`RadiusPill`, padding `9,3`) with a 7px dot + 12px SemiBold label. Background from `StatusToBadgeBackground` (soft tint), dot/text from `StatusToBrush`, text from `StatusToText`. **Null handling is contractual:** a never-run job renders a neutral "Not run" badge via `FallbackValue` (`NeutralSoftBrush` / `TextMutedBrush`) — a test asserts the literal "Not run" text so a blank status cell is a build failure.
- **`ChangeBadgeTemplate`** (DataContext = `ChangeType`): same pill, text-only, colored by `ChangeTypeToBrush`/`ChangeTypeToBadgeBackground` (Added=success, Modified=accent/warning, Deleted=error family).
- **`ConnectionStatusBadgeTemplate`** (DataContext = `ConnectionTestStatus`): dot + text pill for server test results, via the `ConnectionStatusTo*` converters.
- **`TagChipTemplate` + `TagChipsList`** (environment tags): small pill (padding `8,2`, 11px SemiBold) — neutral (`NeutralSoftBrush` + muted text) by default; **production tags render red** (`ErrorSoftBrush` background + `ErrorBrush` text) via an `IsProduction` trigger. `TagChipsList` is the wrapping `ItemsControl` style used on Dashboard, Jobs, Job Workspace, and History.

---

## 11. Banners (the scheduler-warning pattern)

Inline page banners for conditions the user must know about, used identically on Dashboard, Jobs, and the Job Workspace for scheduler health:

```
Border: WarningSoftBrush fill, RadiusControl, Padding 14,10
  └ horizontal: IconWarning glyph (WarningBrush, 15px, top-aligned)
              + Body text, wrapping, MaxWidth 900
Visibility: bound to the message with NullToVisibility (message null = no banner)
```

Sits between the page header and the content. Same anatomy with `ErrorSoft`/`ErrorBrush` for error banners. Banners state the condition *and* what to do about it in the text itself.

---

## 12. Empty states

Used on Jobs, Servers, Repositories, History, etc., overlaid in the same card as the (hidden) grid and shown when the collection count is 0:

1. **Icon chip:** 52×52 `PanelBrush` border, radius 14, containing the section's icon at 24px muted;
2. **Headline:** `SectionTitle`, centered, 16px above-margin ("No sync jobs yet");
3. **Description:** `Muted`, centered, one sentence of guidance;
4. **Action:** a `PrimaryButton` performing the obvious next step ("Create Sync Job"), 18px below.

The whole stack is centered, `MaxWidth 400`. Empty states always offer the action, not just the observation.

---

## 13. Loading & busy states

- View models expose `IsBusy`; controls that must not be pressed mid-operation bind `IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}"`. This is the standard disable pattern across the wizard, Add windows, Settings, and row actions (row actions reach the page VM via `RelativeSource AncestorType=UserControl`).
- Ongoing work shows an **indeterminate `ProgressBar`, 4px tall** (run progress in the Job Workspace, diff loading) with a status text line — no spinners, no overlays.
- Long operations keep the page usable; only the conflicting controls disable.

---

## 14. Dialogs, tooltips, accessibility

### AppDialog (confirm / error)

`Views/AppDialog.xaml` — the themed replacement for `MessageBox`:

- Borderless (`WindowStyle=None`, transparent, `SizeToContent`), 424px content, `CenterOwner`, no taskbar entry.
- **Shadow on a separate, text-free layer:** a sibling `Border` carries the `DropShadowEffect`; the content border on top has no effect, so ClearType is preserved (§15).
- Anatomy: 42×42 soft-tinted icon chip (warning/error variants) + `SectionTitle` title, `Muted` message, right-aligned `SubtleButton` Cancel + `PrimaryButton` confirm.
- **Keyboard:** confirm is `IsDefault` (Enter), cancel is `IsCancel` (Esc). Always.

### Modal Add/Edit windows

`AddServerWindow`, `AddRepositoryWindow`, `CreateJobWindow` (the wizard): real windows, `WindowStartupLocation=CenterOwner`, `ResizeMode=NoResize` (fixed-purpose forms), `ShowInTaskbar=False`, ~560px wide with `SizeToContent=Height`, `Margin="28"` content. Header = `PageTitle` + `Muted` subtitle; footer = Cancel (`SubtleButton`, `IsCancel`) then the primary action. Every window sets the standard window attributes (§15).

### Tooltips & automation names

- Every `IconButton` (glyph-only, no visible label) **must** set both a `ToolTip` and `AutomationProperties.Name` — the tooltip for sighted users, the automation name for screen readers. State-dependent tooltips use a style trigger (see the nav collapse toggle).
- Buttons with visible text labels need neither unless the label is ambiguous.

---

## 15. Structural rules (what keeps the app calm)

- **Six navigation sections, fixed:** Dashboard, Servers, Repositories, Jobs, History, Settings. New surfaces do not get nav items; they live inside an existing section (Diagnostics, audit, alerts, proxy all live in Settings). The rail is collapsible (224px ↔ 64px icon-only).
- **Everything job-related lives in the Job Workspace** (`JobDetailView`): runs, changes, logs, dependencies, configuration, diff entry points. The Jobs page is a list; drilling in opens the workspace — no job features scattered elsewhere.
- **Settings uses per-section Save:** each card (Git commits, retention, alerts, proxy, production tags, …) has its own Save button acting on that card only. There is no global Save/dirty state spanning the page.
- **One page title per page**, subtitle in `Muted`, actions right-aligned in the header row.
- Toasts (bottom-right, `MainWindow`) are the only floating UI: card-styled, 380px, with a 4px severity edge bar (error/warning/accent) *plus* a matching icon (never color-only), title/message/action, and a dismissible `IconButton`.

### Engineering rules learned in this repo

1. **Each merged theme dictionary must be self-contained.** A dictionary must merge every dictionary whose resources its styles reference via `StaticResource` (`Typography.xaml` merges `Colors.xaml`; `Controls.xaml` merges Colors + Typography + Icons). Relying on a *sibling* merge in `Theme.xaml` fails when styles are sealed — `StaticResource` across sibling dictionaries resolves at runtime in the app but breaks at style-seal time (and in the headless tests). Duplicate merges are cheap; broken seals are not.
2. **Never put a `DropShadowEffect` on a `Border` that contains text.** A bitmap effect forces WPF to rasterize the container *and its text* into an intermediate layer, dropping ClearType — text goes blurry. Cards use hairline borders instead; `AppDialog` shows the sanctioned workaround (shadow on an empty sibling layer).
3. **Every `Window` sets** `UseLayoutRounding="True"`, `TextOptions.TextFormattingMode="Display"`, `TextOptions.TextRenderingMode="ClearType"`, plus `FontFamily="{StaticResource AppFontFamily}"`, `Foreground="{StaticResource TextPrimaryBrush}"`, and `Background="{StaticResource AppBackgroundBrush}"` (opaque windows). This is what keeps hairlines and small text crisp at all DPI scales.
4. **The design-system contract is a test.** `DesignSystemTests.App_LoadsResourcesAndRendersEveryView` builds the real `App`, asserts the `ExpectedKeys` resource list resolves, and measures/arranges every view (template application is where WPF surfaces unset foregrounds and broken resource references). Windows (wizard, Add dialogs) are not covered by the view render loop — render-probe their `.Content` or eyeball them.

---

## 16. Contributing UI — checklist

Before merging any new control, view, or template:

- [ ] **Reuse tokens.** Colors from `Colors.xaml` brushes only — no hex literals, no new colors. Radii/spacing from the tokens; type from the §3 styles; glyphs via named resources in `Icons.xaml`.
- [ ] **Reuse styles.** Pick the button from §5 by emphasis (one `PrimaryButton` per scope); let implicit input/grid/tab styles apply; wrap content in `Card`/`Panel`.
- [ ] **Grids:** read-only, star columns with `MinWidth` + `CharacterEllipsis`, fixed action columns, never enable horizontal scrolling.
- [ ] **Status is never color-only:** dot/glyph + text; use the shared badge/chip templates instead of new ones; handle the null case (see `StatusBadgeTemplate`'s `FallbackValue`).
- [ ] **Busy & empty:** disable via `IsBusy` + `InverseBool`; empty collections get the §12 icon/headline/description/action stack.
- [ ] **Accessibility:** `ToolTip` **and** `AutomationProperties.Name` on every icon-only button; `IsDefault`/`IsCancel` on dialog buttons; keep `PART_SelectedContentHost` and other PART names intact; keyboard focus must show (accent border on inputs comes free from the implicit styles).
- [ ] **New window?** Set the §15.3 window attributes; `CenterOwner`, `ShowInTaskbar=False` for modals.
- [ ] **New theme dictionary or resource?** Merge what it references (self-containment rule); no `DropShadowEffect` on text-bearing containers.
- [ ] **Render-test it.** Add new views to `DesignSystemTests.Views()`; add load-bearing new resource keys to `ExpectedKeys`; for windows, render-probe the content. `dotnet test` must pass — the render smoke test is the design system's enforcement mechanism.
- [ ] **Structure:** no new nav sections; job-related UI goes in the Job Workspace; new settings get a card with their own Save.
