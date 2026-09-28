# Timekeeper interface

The Windows interface uses warm ivory, dark green ink, muted teal and small brass accents. Serif display headings and monospaced hour totals provide a restrained reference to printed ledgers; the controls, spacing and workflow remain contemporary.

## Shared styles

`src/Timekeeper.App/App.xaml` owns the palette, system-font choices and shared WPF control styles. Use these resources in dialogs as well as the main window. Georgia is reserved for display headings, Segoe UI for controls and body text, and Consolas for totals, short section labels and technical details. These fonts are available on the supported Windows platform without a separate download.

The main workspace groups the read controls, four hour totals, two file-exchange targets, and the final review in that order. The numbered steps distinguish exchange from submission. Settings uses separate cards for accounts, the shared agent, daily defaults and internal work. Its connection result and save actions stay visible while the fields scroll.

Controls provide hover and keyboard-focus feedback; disabled actions use a neutral fill and remain legible. Selection stays readable when a table loses focus. Status messages use text as well as color. Private-update access errors expand their help, and Quickbase diagnostics remain selectable and copyable. Drag feedback indicates when a proposal can be accepted; file picker and paste actions provide alternatives.

The window supports a minimum of 1040 × 720 logical pixels. Long workflows scroll vertically, review tables scroll horizontally where necessary, and long descriptions remain available through row details. Native Windows title bars and system file pickers retain their standard behavior.

## Visual verification

Run the application's `--smoke-test` command against a dedicated artifact folder after building. It renders the initial workspace, sample day at standard and minimum sizes, scrolled review, empty/populated history, settings and errors, updater states, and details/proposal dialogs. Fixtures contain only synthetic data; this mode never reads credentials or makes live service calls. Inspect the images as well as the smoke report before publishing a visual change.
