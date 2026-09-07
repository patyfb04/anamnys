---
title: "The Anamnys Engineering Handbook"
subtitle: "A ground-up guide to the anamnys-aspire codebase"
author: "Anamnys Engineering"
date: "September 2026"
---

# Welcome

This handbook exists to take a developer from zero to productive on **Anamnys** — a
specialty-native clinical note drafting system for solo and small-group mental health and
physical therapy practices — by teaching the codebase the way it was built, not just the
way it looks today.

It is written chapter by chapter, the way you might build the system up yourself: first
the product and the ground rules it operates under, then the environment, then the
orchestration layer, then identity, then data, then the four front-end applications, and
finally the conventions and quirks that keep the whole thing coherent.

Every chapter is grounded in the code as it exists in the repository at the time of
writing (branch `develop`, September 2026), not in aspiration. Where a feature is planned
but not yet built, the text says so explicitly — this is one of the more unusual things
about this codebase: large parts of its data model and its front-end already describe
functionality (real-time transcription hubs, insurance billing, a provider marketplace)
that has no server-side implementation yet. Knowing which is which is, itself, one of the
most important things a new engineer needs to learn here, so it is called out throughout
rather than glossed over.

## How to read this

You don't have to read it start to finish. If you already know what Aspire is and just
need the authentication system explained, skip to Part 4. If you're about to touch the
provider app, go straight to Chapter 13. But if this is genuinely your first week, reading
in order will save you from the trap this repository sets most often for newcomers:
assuming that because something is *referenced* in the code, it must *exist*.

## Table of contents

**Part 1 — Orientation**
1. Introduction & Product Vision
2. Repository & Solution Layout

**Part 2 — Getting It Running**
3. Environment Setup & First Run

**Part 3 — The Backend Platform**
4. .NET Aspire Orchestration (the AppHost)
5. The Server Project

**Part 4 — Identity & Access**
6. Authentication Architecture: the BFF Pattern
7. Keycloak in Practice
8. Session Lifecycle in Code

**Part 5 — Data**
9. Data Access & EF Core Today
10. The Full Data Model

**Part 6 — Frontend**
11. Frontend Architecture Overview
12. The Shared Package
13. Walkthrough: `apps/provider`
14. Walkthrough: `apps/web`, `apps/patient`, `apps/admin`
15. The Keycloak Theme

**Part 7 — Quality, Conventions & Shipping**
16. Testing Strategy
17. Deployment Shape
18. Conventions & Anti-Patterns

**Part 8 — Reference**
19. Gotchas Cheat-Sheet

## Building a single PDF from these files

The chapters are plain Markdown, numbered so a simple glob sorts them in reading order, and
each chapter uses exactly one top-level (`#`) heading — its title — so a PDF tool that
starts a new chapter at each `#` will do the right thing automatically.

If you have [pandoc](https://pandoc.org) with a PDF engine available (either a LaTeX
distribution, or `wkhtmltopdf`/`weasyprint`), the whole book is one command from inside this
`documentation/` folder:

```bash
pandoc 0*.md 1*.md \
  -o anamnys-handbook.pdf \
  --toc --toc-depth=2 \
  --number-sections \
  --top-level-division=chapter \
  -V geometry:margin=1in \
  -V documentclass=report
```

If you don't have a LaTeX engine installed, swap in a non-LaTeX PDF engine:

```bash
pandoc 0*.md 1*.md -o anamnys-handbook.pdf --toc --pdf-engine=weasyprint
```

Either way, pandoc reads the YAML block at the top of this file as the document's title-page
metadata because it's the first file in the glob — keep this file first alphabetically if you
add chapters later.
