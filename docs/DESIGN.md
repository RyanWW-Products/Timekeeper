# Timekeeper interface

The Windows interface offers five coordinated themes: Ivory (the original warm light palette), Dark (neutral charcoal), Slate (cool light), Forest (deep green) and Sand (warm neutral). Serif display headings and monospaced hour totals provide a restrained reference to printed ledgers; the controls, spacing and workflow remain contemporary. Labels describe the action or state directly, without decorative taglines.

## Shared styles

`src/Timekeeper.App/Appearance.cs` owns semantic palettes, accent overrides, interface scale and spacing preferences. `App.xaml` provides initial resources, system-font choices and shared WPF control styles. Use semantic dynamic resources in XAML and the shared resource brushes in code-built dialogs; the latter update through bound colors without freezing. Georgia is reserved for display headings, Segoe UI for controls and body text, and Consolas for totals, short section labels and technical details. These fonts are available on the supported Windows platform without a separate download.

The Appearance window previews changes across open windows. Cancel restores the opening preferences; Save atomically replaces `%LOCALAPPDATA%\Timekeeper\appearance.json`. These UI-only preferences do not participate in account verification or read-session fingerprints. Missing, damaged or unsupported preferences fall back to the original Ivory defaults. Accent choices are theme color, Teal, Blue, Violet and Amber, with separate light/dark variants. Interface size is 100%, 110% or 120%, and spacing is Comfortable or Compact.

The main workspace groups the read controls, four hour totals, two file-exchange targets, and the final review in that order. The numbered steps distinguish exchange from submission. Settings uses separate cards for accounts, the shared agent, daily defaults and internal work. Its connection result and save actions stay visible while the fields scroll.

Controls provide hover and keyboard-focus feedback; disabled actions use a neutral fill and remain legible. Selection stays readable when a table loses focus. Status messages use text as well as color. Private-update access errors expand their help, and Quickbase diagnostics remain selectable and copyable. Drag feedback indicates when a proposal can be accepted; file picker and paste actions provide alternatives.

The window supports a minimum of 1040 × 720 logical pixels. Long workflows and navigation scroll vertically, review tables scroll horizontally where necessary, and long descriptions remain available through row details. The app themes date/calendar controls, selection menus, disabled controls and table rows. Native title-bar colors follow the theme where Windows supports them; system file pickers retain Windows behavior.

## Visual verification

Run the application's `--smoke-test` command against a dedicated artifact folder after building. It renders the initial workspace, sample day at standard and minimum sizes, scrolled review, empty/populated history, settings and errors, updater states, and details/proposal dialogs. It also renders all five themes across the workspace, existing settings dialog, appearance picker and calendar, plus dark dialogs and 120% Compact layouts. Checks cover preview cancellation, save/reload, invalid preference fallback, session preservation and a 4.5:1 minimum for the principal text/surface pairs in all 25 theme/accent combinations. Fixtures contain only synthetic data; this mode never reads credentials or makes live service calls. Inspect the images as well as the smoke report before publishing a visual change.
