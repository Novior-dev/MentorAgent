# Accessibility

This statement covers the chat widget of **Novior.MentorAgent** (the `ChatWidget` component of
`Novior.MentorAgent.Abstractions` and its stylesheets), version 1.5.0. It follows the model of the accessibility
statement of Directive (EU) 2016/2102, adapted to a library: the widget is part of your application, so the
statement of your site or app is yours to publish, and what follows tells you what you can say about the widget in it.

## Compliance status

The widget **meets WCAG 2.1 level A and AA in what we tested**, the level EN 301 549 v3.2.1 requires for web content:
automated checks, the keyboard and the focus in the browser, and Narrator on Windows. It is **partially conformant**:
other screen readers and some content are outside what we tested (see [Known limits](#known-limits)).

## How it was tested

- **Automated, on every build:** [axe-core](https://github.com/dequelabs/axe-core) 4.10.3, with the WCAG 2.1 A and AA
  rules, in a real Chromium browser, in six states of the widget: closed, open, while a reply streams, with a
  confirmation waiting, with the onboarding tour, and in Japanese. Every theme (Default, Dark, Minimal) is measured over
  a light and a dark page, idle and busy. No violation is accepted.
- **What axe does not see, tested in the browser on every build:** the keyboard (every control reached and used without
  a mouse, Esc closing one layer at a time and never answering a confirmation); where the focus goes when the panel
  opens and closes, after a reply, on a confirmation and on the tour; the closed panel out of the tab order (`inert`),
  and the rest of the panel while the tour is open, Tab going round the tour's buttons; the conversation announced as a
  log, once a reply is whole, each message saying who speaks; images and icons that are decoration hidden from a screen
  reader; reduced motion; a narrow screen (320 CSS px) and a 1280 px window at 200% zoom with no horizontal scroll
  (WCAG 1.4.10).
- **By hand, before the release:** a sample application used with the keyboard only and with Narrator, in Edge on
  Windows 11: the roles, names and states of the controls, the confirmation, the tour, and each reply read whole once.
- **Colour contrast:** every text colour of the three themes computed against the panel's surfaces, 4.5:1 or more.

## Known limits

- **The panel's background.** By default the panel is nearly opaque (`--bm-bg-panel` at 96%), so the contrast of its
  text does not depend on your page. If you set it back to a translucent glass, as up to 1.4, the contrast depends on
  what is behind the panel: checking it is then yours.
- **Your colours.** `PrimaryColor`, a custom theme or your own values of the `--bm-*` variables can lower the contrast
  below 4.5:1. The widget does not check them.
- **What the model writes.** The widget renders the assistant's replies (text, lists, tables, links) accessibly, but it
  cannot make their content clear, nor give a meaningful description to an image the model or a tool returns.
- **Screen readers.** Of the screen readers, only Narrator was used; NVDA, JAWS, VoiceOver and TalkBack were not.
- **Phones.** When the panel opens, the focus goes to the message field, so on a phone the on-screen keyboard opens
  over the panel.
- **Right-to-left languages.** The widget's languages are written left to right; Arabic and Hebrew are not supported.
- **Voice.** Voice input and output depend on the browser's speech services; they are an addition to the keyboard and
  the text, never the only way to do something. With a screen reader, turn the widget's voice off with the speaker
  button in its header: otherwise both read the reply at the same time. The choice is remembered in your browser.
- **Outside the widget.** The operator dashboard, the sample applications and the pages of your application are not
  covered by this statement.

## Feedback and contact

If something in the widget is hard or impossible to use with your assistive technology, please write to
**security@mentoragent.net**, with the version, the browser, the assistive technology and what you did.

- **Within 5 working days** we acknowledge the report and tell you what happens next. This is a best-effort
  commitment, not a contractual one.
- A barrier we confirm is fixed like any defect, following [SECURITY.md](SECURITY.md#supported-versions): in a patch
  release of the latest minor version, grouped with other fixes, and in the next minor version.

## Preparation of this statement

Prepared on 26 November 2026 by the maintainers, by self-assessment, with the tests above. It is
reviewed at every release that changes the widget.
