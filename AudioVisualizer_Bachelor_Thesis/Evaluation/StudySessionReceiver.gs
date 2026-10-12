/**
 * Companion receiver for StudySessionManager.cs's remote submission.
 *
 * Setup:
 *   1. Create a new Google Sheet (this will hold one row per participant).
 *   2. Extensions -> Apps Script. Delete any placeholder code and paste this file's contents in.
 *   3. Deploy -> New deployment -> select type "Web app".
 *        - Execute as: Me
 *        - Who has access: Anyone
 *      (This does NOT expose the Sheet itself publicly -- only this one
 *      doPost() endpoint, which can only append a row in the exact shape
 *      below. Nobody can read existing rows through it.)
 *   4. Click Deploy, authorize the script when prompted, then copy the
 *      "Web app URL" it gives you.
 *   5. Paste that URL into the submissionEndpointUrl field on the
 *      StudySessionManager component in Unity.
 *
 * Every session (in-person or remote, Android or Windows) that has this
 * URL configured will append one row here automatically once the
 * participant finishes all targetLevelCount levels. Match rows to your
 * external survey responses via participantId.
 */
function doPost(e) {
  var sheet = SpreadsheetApp.getActiveSpreadsheet().getActiveSheet();

  if (sheet.getLastRow() === 0) {
    sheet.appendRow([
      "participantId",
      "startCondition",
      "conditionSequence",
      "levelDurations_s",
      "timestamp",
      "receivedAt"
    ]);
  }

  var p = e.parameter;
  sheet.appendRow([
    p.participantId || "",
    p.startCondition || "",
    p.conditionSequence || "",
    p.levelDurations_s || "",
    p.timestamp || "",
    new Date().toISOString()
  ]);

  return ContentService.createTextOutput("OK");
}
