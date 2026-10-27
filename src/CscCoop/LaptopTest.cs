using CoopCore;
using UnityEngine;

namespace CscCoop;

// CSCCOOP_TEST=laptop (tools/run-pair.ps1 -Test laptop): both start a new game from the co-op screen, play into
// the first job and leave it (everyone leaves) to get to the hub. There the story's next mail reaches both
// laptops (MissionController.AddNewMail, what sleeping and the phone call lead to in play; the test saves have
// no later story). The host accepts it on its laptop; the client's laptop accepts it too and reports it; the
// host's truck refuses a job the client lacks, then starts the accepted one; the client follows, both share it.
public partial class Bot
{
    int lstep; float lAt, lDeadline; int lJob = -1;
    float lReport = -1;

    void LaptopTest(string scene, float inScene, GameWorld w)
    {
        var ses = CoopRunner.Instance?.Session;
        if (ses == null || w.Player == null) return;
        bool host = ses.IsHost;
        float now = Time.unscaledTime;
        bool hub = scene.Contains("Hub") && !scene.Contains("GameIntro");
        var report = Object.FindObjectOfType<MissionReportFinalView>();
        if (report != null && report.isActiveAndEnabled)
        {
            if (lReport < 0) lReport = now;
            else if (now - lReport > 8) { Plugin.L.LogInfo("LAPTOPTEST closing the report"); report.OnCloseReport(); lReport = -1; }
            return;
        }
        lReport = -1;
        if (now < lAt || !w.Player.CanMove) return;
        switch (lstep)
        {
            case 0 when !hub && !scene.Contains("Loading") && !scene.StartsWith("MenuScreen") && PartnerHere(ses, w):
                // Loaded inside a job: the host leaves, everyone leaves with it.
                Plugin.L.LogInfo($"LAPTOPTEST the save loaded into a job ({scene}): leaving it");
                if (host) Object.FindObjectOfType<MissionLeaveController>()?.LeaveMissionWithReport();
                lAt = now + 20; break;
            case 0 when hub && inScene > 8:
                Plugin.L.LogInfo($"LAPTOPTEST in the hub ({scene}), partner here {PartnerHere(ses, w)}");
                lstep = 10; lAt = now + 60; break;
            case 10 when PartnerHere(ses, w) || now > lAt:
            {
                // The same next mail on both: the lowest-id mail with a job this save neither has nor was offered.
                var mc = MissionController.Instance;
                MailData next = null;
                var own = Jobs.OwnJobs();
                if (mc?.missions?.mails != null)
                    foreach (var m in mc.missions.mails)
                        if (m != null && m.hasMission && m.missionId != 0 && !own.Contains(m.missionId) && (next == null || m.id < next.id)) next = m;
                if (next != null) { Plugin.L.LogInfo($"LAPTOPTEST the story delivers mail {next.id} (job {next.missionId}) to this laptop"); mc.AddNewMail(next.id); }
                else Plugin.L.LogInfo("LAPTOPTEST no mail to deliver");
                lstep = 1; lAt = now + (host ? 6 : 2); break;
            }
            case 1:
            {
                var mc = MissionController.Instance;
                var mails = new System.Text.StringBuilder();
                MailData pick = null;
                if (mc?.AvailableMails != null)
                    foreach (var m in mc.AvailableMails)
                    {
                        if (m == null) continue;
                        mails.Append($" {m.id}:{(m.hasMission ? m.missionId.ToString() : "-")}{(m.HasBeenAccepted ? "a" : "")}");
                        if (pick == null && m.hasMission && !m.HasBeenAccepted && !Jobs.OwnJobs().Contains(m.missionId)) pick = m;
                    }
                Plugin.L.LogInfo($"LAPTOPTEST partner here {PartnerHere(ses, w)}, mails (id:job, a = accepted):{mails}; own jobs [{string.Join(",", Jobs.OwnJobs())}]");
                if (host)
                {
                    if (pick != null) { lJob = pick.missionId; Plugin.L.LogInfo($"LAPTOPTEST host accepts mail {pick.id} for job {lJob} on the laptop"); GameManager.Instance.AddMission(pick); }
                    else Plugin.L.LogInfo("LAPTOPTEST host has no mail to accept");
                }
                lstep = 2; lAt = now + 10; break;
            }
            case 2:
                if (host)
                {
                    var theirs = Jobs.ClientJobsOf(1);
                    Plugin.L.LogInfo($"LAPTOPTEST client can start [{(theirs == null ? "nothing reported" : string.Join(",", theirs))}], has the host's laptop job {lJob}: {theirs != null && theirs.Contains(lJob)}");
                    int lacking = Lacking(theirs);
                    Plugin.L.LogInfo($"LAPTOPTEST host tries the truck with job {lacking} (the client lacks it)");
                    if (lacking >= 0) TryTruck(lacking);
                }
                else Plugin.L.LogInfo($"LAPTOPTEST client own jobs [{string.Join(",", Jobs.OwnJobs())}]");
                lstep = 3; lAt = now + 6; break;
            case 3:
                if (host && lJob >= 0) { Plugin.L.LogInfo($"LAPTOPTEST host takes the truck to job {lJob}"); TryTruck(lJob); }
                lstep = 4; lAt = now + 2; lDeadline = now + 150; break;
            case 4 when !hub && PartnerHere(ses, w) && GameManager.Instance.ActiveMission != null:
                Plugin.L.LogInfo($"LAPTOPTEST in job {GameManager.Instance.ActiveMission.id} together, scene {scene}");
                lstep = 5; lAt = now + 5; break;
            case 4 when now > lDeadline:
                Plugin.L.LogInfo("LAPTOPTEST never got into a job together");
                lstep = 5; break;
            case 5:
                Plugin.L.LogInfo("BOT done, quitting");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                break;
        }
    }
}
