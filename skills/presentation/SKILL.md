---
name: presentation
description: Create, inspect, revise, and publish PowerPoint presentations in a persistent sandbox.
---

# Presentation

Use this Skill whenever the user asks for a PowerPoint, PPTX, slide deck, slides, or a
presentation file. The user only describes the desired deliverable and content; never ask
them to name tools, paths, Dynamic Sessions, containers, or storage.

## Required tool policy

- Use the `custom:pptx_*` tools for new work. Keep one stable `deckId` for the entire
  conversation and reuse it on every call.
- Use `custom:pptx_files` to write source files, `custom:pptx_run` to run Python or Node.js,
  `custom:pptx_preview` to inspect rendered slides, and `custom:pptx_publish` only for the
  final validated deck.
- The sandbox has Python, python-pptx, Node.js, PptxGenJS, LibreOffice, Poppler, Pillow,
  PyMuPDF, and Noto CJK fonts preinstalled. Do not run `pip`, `npm install`, `apt`, `curl`,
  or any network command.
- `custom:create_presentation` is a legacy deterministic fallback. Use it only when the
  workspace tools are unavailable.
- Do not claim that a presentation was created without a successful publish result.

## Content and story

1. Gather the title, optional subtitle, audience, and content from the conversation. Ask a
   concise clarifying question only when essential content or the intended audience is
   missing.
2. Preserve the user's facts, claims, terminology, language, and level of certainty. Do not
   invent metrics, dates, customers, quotations, sources, conclusions, or other facts.
3. Build a clear audience-appropriate storyline: context or purpose, the supplied key
   points in a logical sequence, and the supplied conclusion or next step when one exists.
   Do not add an unsupported conclusion merely to complete the structure.
4. Keep each slide focused and edit for readability without changing meaning. Use as many
   slides as the content needs while keeping the deck concise.
5. Choose concise titles and coherent visual rhythm. Favor varied cards, process/timeline,
   and callout treatments over generic title-and-bullet layouts. Match tone to the audience
   and retain Japanese text when the user provides or requests Japanese.
6. Use safe ASCII workspace paths and a safe ASCII `.pptx` file name.

## Creation and QA loop

1. Choose a short stable `deckId` using ASCII letters, digits, `-`, or `_`.
2. List the workspace before changing it. Reuse existing source and deck files when revising
   a presentation from an earlier turn.
3. Write a generation script with `custom:pptx_files`. Prefer PptxGenJS for new decks and
   python-pptx when editing or when it better fits the task.
4. Run the script with `custom:pptx_run`. Treat a nonzero exit code as failure and inspect
   both stdout and stderr.
5. Call `custom:pptx_preview`. Inspect every returned slide image and actively look for:
   overlap, clipped or overflowing text, weak contrast, excessive wrapping, poor spacing,
   inconsistent alignment, missing content, and leftover placeholders.
6. Make at least one concrete correction based on the first visual pass, rerun the script,
   and preview the affected deck again. One fix may introduce another problem.
7. Repeat until a complete visual pass finds no new issue.
8. Call `custom:pptx_publish` only after the final preview validates successfully.

## Design requirements

- Do not create plain title-and-bullet decks. Every content slide needs a meaningful visual
  element such as a chart, diagram, image, icon treatment, process, timeline, comparison,
  card grid, or large statistic.
- Use a topic-specific palette with one dominant color, one or two supporting tones, and
  one accent. Vary layouts while preserving a coherent visual motif.
- Use dark/light contrast intentionally, 0.5 inch minimum outer margins, and consistent
  0.3–0.5 inch gaps.
- Use 36–44 pt slide titles, 20–24 pt section headers, 14–18 pt body text, and strong
  contrast. Do not center body paragraphs.
- Never add decorative accent lines directly under titles.
- Preserve Japanese text when requested and use installed Noto CJK fonts.

## Completion contract

Claim success only after `custom:pptx_publish` returns a downloadable PPTX artifact. If
execution, validation, rendering, visual inspection, correction, or publish fails, report
the failure rather than presenting partial output as complete.
