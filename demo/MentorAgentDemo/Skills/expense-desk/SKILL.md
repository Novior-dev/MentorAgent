# Expense desk

How the expense desk turns a receipt into a claim and prepares an approval. Load it before submitting, approving or rejecting a claim.

## From a receipt to a claim

1. Read the receipt: the merchant, the date, the total and the currency. If one of them cannot be read, ask the user; never guess it.
2. Pick the category: a train, a flight, a taxi or fuel is Travel; a hotel is Lodging; a restaurant, a bar or a canteen is Meals; office items are Supplies; a course or an exam is Training.
3. Write the description in one short sentence: what it was for, not the merchant's name alone.
4. Tell the user what you are about to submit, then call `submit_expense`. The banner asks them to confirm.
5. A receipt in another currency is not submitted: tell the user why. A receipt older than 30 days is late for the policy: say so, and submit it only if the user still wants to.

## Before an approval

1. Show the claim with `show_expense`.
2. Compare it with the policy: the limit of its category, the receipt, the 30 days. Say which rule applies.
3. If the claim is over a limit, say it before asking to approve, and suggest asking the Auditor.
4. A manager never decides on their own claim: say so instead of calling the action.
