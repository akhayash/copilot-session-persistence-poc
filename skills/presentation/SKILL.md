---
name: presentation
description: Create a validated, rendered PowerPoint presentation from user-provided content.
---

# Presentation

Use this Skill whenever the user asks for a PowerPoint, PPTX, slide deck, slides, or a
presentation file. The user only describes the desired deliverable and content; never ask
them to name tools, paths, Dynamic Sessions, containers, or storage.

## Required tool policy

- Use **only** `custom:create_presentation` to create a presentation.
- Never use `execute_python`, shell commands, package managers, `pip`, or another
  presentation-generation tool.
- Do not claim that a presentation was created without a successful tool result.

## Content and story

1. Gather the title, optional subtitle, audience, and content from the conversation. Ask a
   concise clarifying question only when essential content or the intended audience is
   missing.
2. Preserve the user's facts, claims, terminology, language, and level of certainty. Do not
   invent metrics, dates, customers, quotations, sources, conclusions, or other facts.
3. Build a clear audience-appropriate storyline: context or purpose, the supplied key
   points in a logical sequence, and the supplied conclusion or next step when one exists.
   Do not add an unsupported conclusion merely to complete the structure.
4. Submit 2–8 total slides, including the title slide. Therefore provide 1–7 content slides.
   Keep each slide focused, edit for readability without changing meaning, and put genuinely
   important supplied text in `highlight`.
5. Choose concise titles and coherent visual rhythm. Favor varied cards, process/timeline,
   and callout treatments over generic title-and-bullet layouts. Match tone to the audience
   and retain Japanese text when the user provides or requests Japanese.
6. Use a safe ASCII `.pptx` file name. Pass no code, commands, URLs for execution, or paths.

## Completion contract

Call `custom:create_presentation` once the content is ready. Claim success only when its
manifest reports top-level `validationPassed: true` and returns all of the following:

- one PPTX;
- one rendered PDF;
- one PNG for every slide;
- one downloadable `validation.json` audit artifact;
- a slide count matching the requested 2–8 total slides; and
- file metadata including nonzero sizes and SHA-256 hashes.

If any item is absent, validation does not pass, rendering fails, or counts differ, report
that creation failed rather than presenting partial output as complete.
