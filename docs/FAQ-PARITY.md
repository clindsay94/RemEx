# FAQ canonical question list

Both platforms must answer all sixteen. The pairing below is by MEANING, not by number: the two FAQs were written independently and their numbering diverged early, which is why 'Faq_Q10' and 'faq_q10' are unrelated questions. Match on this table, never on the index.

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

## Rules

1. **Adding an entry means adding it to BOTH platforms**, in all 9 locale files each, and to this table. The PC enumerates Faq_Q1..N in AboutViewModel; Android lists FaqItem entries explicitly in FaqScreen.kt. Both counts must be raised.
2. **Answers are ported, not re-authored.** Writing the same answer twice from scratch is how the port-fallback and DXGI inaccuracies came to exist on only one side. Start from the existing platform's text and change only what is genuinely platform-specific.
3. **Navigation IS platform-specific.** Android's 'Open the More tab and tap Personalization' is wrong on the PC, which uses the Personalize panel; the PC's Settings paths are wrong on Android. Port the substance, rewrite the route.
4. The numbering will keep diverging as entries are added. That is tolerable as long as this table is updated; renumbering either platform to match the other would break every existing translation.
