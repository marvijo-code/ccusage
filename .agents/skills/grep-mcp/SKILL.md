---
name: grep-mcp
description: Searches public GitHub repositories for real-world code through grep.app. Use when unfamiliar APIs, syntax, integrations, or production usage patterns need concrete examples.
---

# grep.app MCP

Call the project-scoped server through `mcporter`:

```sh
mcporter call 'grep.searchGitHub(query: "<literal-code-pattern>")'
```

Search for code that would literally appear in a file, not conceptual keywords or questions. Narrow noisy results with `repo`, `path`, or `language`; use `useRegexp: true` only for an intentional regular expression.

Example with filters:

```sh
mcporter call 'grep.searchGitHub(query: "useState(", language: ["TypeScript", "TSX"])'
```
