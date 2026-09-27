# FAQ canonical question list

Both platforms must answer all twenty-two. The pairing below is by MEANING, not by number: the two FAQs were written independently and their numbering diverged early, which is why 'Faq_Q10' and 'faq_q10' are unrelated questions. Match on this table, never on the index.

| # | Question | PC | Android |
|---|----------|----|---------|
| 1 | What do I need to run on my PC? | Faq_Q1 | faq_q1 |
| 2 | How do I pair my phone / PC? | Faq_Q2 | faq_q5 |
| 3 | How do I find my PC's IP address? | Faq_Q3 | faq_q2 |
| 4 | Auto-discovery isn't finding my PC | Faq_Q4 | faq_q3 |
| 5 | What is the default port? | Faq_Q5 | faq_q4 |
| 6 | Can I connect over the internet? | Faq_Q6 | faq_q6 |
| 7 | Remote Desktop is laggy | Faq_Q7 | faq_q7 |
| 8 | What is Wake-on-LAN? | Faq_Q8 | faq_q8 |
| 9 | How do I transfer files? | Faq_Q9 | faq_q12 |
| 10 | Can I lock my PC remotely? | Faq_Q10 | faq_q16 |
| 11 | How do I set up Tailscale? | Faq_Q11 | faq_q11 |
| 12 | Phone refuses to connect / asks to re-pair | Faq_Q12 | faq_q13 |
| 13 | Why does RemEx need elevated permission? | Faq_Q13 | faq_q14 |
| 14 | How do I stop RemEx starting at sign-in? | Faq_Q14 | faq_q15 |
| 15 | How do I change how RemEx looks? | Faq_Q15 | faq_q9 |
| 16 | Can I watch the tutorial again? | Faq_Q16 | faq_q10 |
| 17 | What are routines? | Faq_Q17 | faq_q17 |
| 18 | Why didn't my routine run? | Faq_Q18 | faq_q18 |
| 19 | Does RemEx track my location? | Faq_Q19 | faq_q19 |
| 20 | Can a routine shut down my PC while I'm using it? | Faq_Q20 | faq_q20 |
| 21 | Can I edit routines on my PC? | Faq_Q21 | faq_q21 |
| 22 | How do NFC tags work with routines? | Faq_Q22 | faq_q22 |

**Routines (RemEx-pp0rt.11):** rows 17-22 shipped on the PC first and are now on Android too
(`faq_q17`..`faq_q22` in all 9 `values*/strings.xml`, six more `FaqItem`s in `FaqScreen.kt`), using
the Android copy drafted in `docs/specs/2026-09-26-routines-design.md` §5.4 with the same D1/D3/D8
adjustments the PC copy made (the countdown still runs for a phone-started or NFC routine; lock and
display-off never count down; the PC can block a phone's routines but not delete them). A future
row that ships on one platform first can still be marked `(pending)`; the parity check treats such a
cell as not yet required.

The Routines entries follow the routine rules of spec §0.2, not the drafts where they differ: every
routine-issued shutdown, restart, sign-out, sleep or hibernate counts down on the PC (D1), lock and
display-off never do, and PC Run now asks for confirmation instead (D3). Port those facts, not
older wording.

## Rules

1. **Adding an entry means adding it to BOTH platforms**, in all 9 locale files each, and to this table. The PC enumerates Faq_Q1..N in AboutViewModel; Android lists FaqItem entries explicitly in FaqScreen.kt. Both counts must be raised.
2. **Answers are ported, not re-authored.** Writing the same answer twice from scratch is how the port-fallback and DXGI inaccuracies came to exist on only one side. Start from the existing platform's text and change only what is genuinely platform-specific.
3. **Navigation IS platform-specific.** Android's 'Open the More tab and tap Personalization' is wrong on the PC, which uses the Personalize panel; the PC's Settings paths are wrong on Android. Port the substance, rewrite the route.
4. The numbering will keep diverging as entries are added. That is tolerable as long as this table is updated; renumbering either platform to match the other would break every existing translation.
