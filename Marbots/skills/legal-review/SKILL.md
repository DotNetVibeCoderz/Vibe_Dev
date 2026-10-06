---
name: legal-review
description: Contract and policy review against a checklist with clause references. Informational only, not legal advice.
version: 1.0.0
requires:
  tools: [read_file, write_file]
permissions:
  network: false
  shell: false
---
# Legal document review (not legal advice)

Checklist for contracts:
- Parties, effective date, term & renewal, termination (for cause/convenience, notice).
- Scope/deliverables, acceptance, service levels.
- Fees, payment terms, late fees, taxes, currency.
- IP ownership & licenses, confidentiality, data protection (UU PDP / GDPR), security obligations.
- Warranties, disclaimers, limitation of liability (caps, exclusions), indemnities.
- Governing law & dispute resolution (court/BANI/arbitration), language clause (UU 24/2009 for Indonesia).
- Assignment, subcontracting, force majeure, notices, entire agreement.

## Output
| Clause | Summary | Risk (H/M/L) | Note / suggested revision |
Then: key obligations & deadlines list, missing clauses, questions for counsel.
Always end with: "This review is informational and is not legal advice."
