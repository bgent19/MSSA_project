# MSSA_project

## Issue tracker

This repo uses the **local markdown** tracker. Specs, issues, and wayfinder maps
live under `.scratch/<feature-slug>/` — see `issue-tracker-local.md` in the
matt-pocock skills for the conventions.

The current effort is `.scratch/mini-project/`: the map is `map.md` and tickets
are `issues/NN-<slug>.md`.

## Wayfinder

Wayfinder sessions run on `main`, in the main checkout. Do not create git
worktrees or feature branches for map or ticket work.

The map and its tickets are the shared tracker, and they are version-controlled
files. Branching them breaks claiming across sessions: a `Status: claimed` on one
branch is invisible to a session on another, so two sessions can silently work
the same ticket. Branching also collides ticket numbers (two sessions both take
`NN-`) and conflicts `map.md` on every resolution.

Claim and resolve tickets on `main`, committing at both points. Branches are for
`/implement` later; `/implement` should not edit the map.
