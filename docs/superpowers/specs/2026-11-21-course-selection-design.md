# Course selection (수강신청) design

## Intent
Curriculum staff collect each student's elective choices through a personal link, then see in Gyomutalk how many chose
each course per elective group and which students have not submitted. Each school runs it on its own.

## Constraints
- The school server is LAN-only; students reach a Google Apps Script web app instead.
- The school's Google Workspace account cannot deploy to "Anyone", so each school uses a dedicated personal Google account.
- OAuth auto-deployment is out (sensitive-scope verification, 100-user cap for unverified apps).
- Student names must not be readable in Google.
- Only teachers given the "교육과정 담당" duty see the menu, roster and results (administrators are not implied).
- The administrator can switch the whole feature off ("courses" in optional menus).

## Approaches
1. Teacher PC talks to Google, server stores data: two hops, the script secret on teacher PCs.
2. **School server talks to Google (chosen)**: the server PC already reaches the internet for updates; the teacher program only talks to its school server.
3. OAuth deployment from the program: rejected (see constraints).

## Design
- The server generates the Apps Script code (secret + page embedded); the teacher pastes it in script.google.com,
  deploys as web app (execute as me, anyone) and pastes the /exec URL back. A version constant tells the program when a re-paste is needed.
- Roster: NEIS .xlsx/.csv upload or pasted rows; header row with 학년/반/번호/성명 (or 학번). Add/delete one student in the program.
  Import replaces the roster of the imported grades; unchanged students keep their links and answers.
- Round: title, grade, closing date, elective groups (name, number to choose, courses).
- "학생 링크 만들기": each student gets a random token and a random AES-GCM key. Google receives token + name encrypted with
  that key + the round. The key travels only in the link fragment (`…/exec?t=TOKEN#KEY`), so Google never sees it.
- Answers are stored in Google by token only. The server pulls them, validates them against the round, and stores them.
- Results: count per course per group, unsubmitted students by class, CSV export of links and results.
- Outbound connections are declared as "course-selection" for script.google.com and script.googleusercontent.com in the deployment review.

## Left to the builder
QR slips, per-course capacity, minimum/maximum counts other than an exact number.
