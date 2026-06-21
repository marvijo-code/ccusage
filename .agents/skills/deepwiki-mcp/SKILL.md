---
name: deepwiki-mcp
description: Reads DeepWiki documentation for public GitHub repositories. Use when exploring an unfamiliar repository's architecture, documentation topics, implementation, or cross-file behavior.
---

# DeepWiki MCP

Use the project-scoped DeepWiki server through `mcporter`.

Inspect available documentation topics before requesting broad content:

```sh
mcporter call 'deepwiki.read_wiki_structure(repoName: "<owner/repo>")'
```

Read the generated repository documentation when its full context is useful:

```sh
mcporter call 'deepwiki.read_wiki_contents(repoName: "<owner/repo>")'
```

For a focused investigation, ask a specific repository-grounded question:

```sh
mcporter call 'deepwiki.ask_question(repoName: "<owner/repo>", question: "<question>")'
```

Use `owner/repo` names for public GitHub repositories. Prefer a focused question over loading all wiki contents when only one subsystem matters.
