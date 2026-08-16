# Motion plans

| # | Plan | Severity | Status |
| --- | --- | --- | --- |
| 001 | Prepare window geometry before first paint | HIGH | DONE |
| 002 | Make Settings page swaps atomic | MEDIUM | DONE |

Recommended order: 001, then 002. The first removes top-level first-paint movement; the second removes intermediate page layouts inside the settled window. Both must land before visual spacing polish so layout evidence stays trustworthy.
