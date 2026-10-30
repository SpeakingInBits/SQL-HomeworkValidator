-- The accounts that are unused in the course data.
SELECT AccountNo, AccountDescription
FROM GLAccounts
WHERE AccountNo IN (100, 110, 120, 162, 167, 181, 200, 205, 221, 230, 234, 235, 237, 238, 239, 241, 242, 243, 251, 280, 290, 300, 301, 302, 306, 310, 394, 500, 505, 506, 508, 527, 528, 532, 536, 541, 546, 548, 550, 551, 555, 556, 565, 568, 569, 576, 590, 610, 611, 620, 621, 630, 631, 632)
ORDER BY AccountNo;
