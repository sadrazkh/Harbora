Last verified: 2026-11-08
Scope: questions the code and git history cannot answer, gathered during the baseline at commit `b3fd974`, plus those still open in `docs/product-audit/18-open-questions.md`.

# Open questions

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). Each question says what this baseline assumed. The first ten are the ones only the owner can answer, most important first.

## For the owner

### OQ-01: Who is the primary customer?
The docs name four audiences. Self-hosting operator and paying reseller pull in different directions: reseller means billing, tenancy and the AI gateway must be right first. **[Assumption]** Both, with reseller as the differentiator (`docs/product-audit/18-open-questions.md` Q2).

### OQ-02: What is the current roadmap, and what defines 1.0?
Nothing newer than the 2026-08 plan exists, and later rounds followed owner requests. **[Assumption]** The order in [ROADMAP.md](ROADMAP.md) is a proposal only.

### OQ-03: Is real customer money flowing through hourly billing today?
This decides how urgent KI-001 to KI-004 are, and whether past charges need review or refunds. **[Assumption]** Treated as high priority.

### OQ-04: When should production's MinIO be upgraded, and is the third-party image acceptable?
`ghcr.io/coollabsio/minio` is built by another project. Production stays on an old image that cannot be pulled again. **[Assumption]** Stay until the upgrade is tested on a copy (D-021).

### OQ-05: Is customer mail hosting a product?
`docs/product-audit/20-final-recommendation.md` says never in core, yet Stalwart provisioning and billing exist. **[Assumption]** It exists and is Partial.

### OQ-06: Is the AI gateway meant to be sold?
It cannot be enabled through the product (KI-060). **[Assumption]** Preview and not sold.

### OQ-07: What licence does the project carry, and which markets is it for?
The README says "License: TBD" and the repository is public with no LICENSE file, so others have no right to use it. The default language is Persian and the default currency is IRR, with no written reason (D-025, D-026). **[Assumption]** Unknown.

### OQ-08: Should the unused Redis container stay?
Every install runs it and nothing connects (KI-052). **[Assumption]** Left alone.

### OQ-09: Which file is the tracker for defects?
`backlog.json` is evidence-checked by a test, but this baseline's defects are in `KNOWN_ISSUES_AND_TECH_DEBT.md`. **[Assumption]** Known-issues holds them until you choose.

### OQ-10: How long should suspended workspaces, and their data, be kept?
The billing design left this open, and no sweep reclaims suspended volumes. **[Assumption]** Data is kept indefinitely.

## Product decisions still open

### OQ-11: Should the two commits with a different author email be rewritten?
`90eb216` and `6316507` carry another address. Fixing them needs a force-push. **[Assumption]** Left.

### OQ-12: Should `Jobs` have a retention period, and how long?
4.2 million rows in production (KI-040). **[Recommendation]** 30 days for completed rows.

### OQ-13: Do the SSL-off controls stay?
KI-015. **[Recommendation]** Remove them unless plain-HTTP apps are wanted.

### OQ-14: Does Create React App keep moving to nginx on its next deploy?
`react-scripts start` can serve in a container, so the rule is a choice (`.superpowers/sdd/r8/p-report.md`). **[Assumption]** Stays.

### OQ-15: What should a backup module restore of type Volume or Application actually do?
KI-022. **[Assumption]** Undefined until the module is finished.

### OQ-16: When does the legacy HTTP agent get removed?
Frozen with no date (`docs/product-audit/18-open-questions.md` Q3). **[Assumption]** Stays.

### OQ-17: Should a marked build variable warn when it would be visible in `docker history`?
Possible in a single-stage image (`.superpowers/sdd/r8/p-report.md`). **[Assumption]** Left as it is.

## Carried over from the 2026-08 audit, still open

From `docs/product-audit/18-open-questions.md`. Numbers are that document's.

| Question | Default recorded there |
|---|---|
| Q5: Do provider webhooks carry the pull-request events previews need? | GitHub and Gitea yes; GitLab needs mapping |
| Q6: Can Kopia use S3 with credentials off the command line? | Native engine stays the S3 path |
| Q7: How should a node's volume snapshot travel? | Presigned HTTPS upload |
| Q8: Should a volume's size limit be enforced or relabelled advisory? | Relabel |
| Q9: Is a third language intended? | Consolidate localization anyway |
| Q10: Has anyone tested on real mobile devices in RTL? | Manual pass later |
| Q12: Should there be a public documentation site? | Later |
| Q13: Should the node agent run on Windows or macOS? | Linux only |
| Q14: What is the ingress tunnel's throughput ceiling? | Benchmark before recommending it |
| Q16: What do `runningWorkloads` and `activeTunnels` mean on the Nodes page? | Open |

## Could not be determined from the repo

- Why Persian was chosen as the default in the first commit, beyond the obvious (D-025).
- How many paying workspaces, nodes or apps production holds.
- The reasoning behind decisions marked `Unknown / reconstructed from code` in [DECISIONS_AND_RATIONALE.md](DECISIONS_AND_RATIONALE.md).
