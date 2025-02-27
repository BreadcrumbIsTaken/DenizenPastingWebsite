﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using FreneticUtilities.FreneticToolkit;
using FreneticUtilities.FreneticExtensions;
using DenizenPastingWebsite.Models;
using DenizenPastingWebsite.Highlighters;
using DenizenPastingWebsite.Utilities;
using DenizenPastingWebsite.Pasting;

namespace DenizenPastingWebsite.Controllers
{
    [RequestFormLimits(ValueLengthLimit = 1024 * 1024 * 30)]
    public class NewController : PasteController
    {
        public NewController()
        {
        }

        public static IActionResult RejectPaste(NewController controller, string type)
        {
            return controller.View("Index", new NewPasteModel() { ShowRejection = true, NewType = type });
        }

        public static HashSet<string> IgnoredOrigins = new()
        {
            "127.0.0.1", "::1", "[::1]"
        };

        /// <summary>Quickly cleans control codes from text and replaces them with spaces.</summary>
        public static string ForceCleanText(string text)
        {
            char[] chars = text.ToArray();
            for (int i = 0; i < chars.Length; i++)
            {
                // ASCII Control codes
                if (chars[i] < 32 || chars[i] == 127)
                {
                    chars[i] = ' ';
                }
            }
            return new string(chars);
        }

        public static IActionResult HandlePost(NewController controller, string type, Paste edits = null)
        {
            if (controller.Request.Method != "POST" || controller.Request.Form.IsEmpty())
            {
                Console.Error.WriteLine("Refused paste: Non-Post");
                return RejectPaste(controller, type);
            }
            IPAddress remoteAddress = controller.Request.HttpContext.Connection.RemoteIpAddress;
            string realOrigin = remoteAddress.ToString();
            string sender = IgnoredOrigins.Contains(realOrigin) ? "" : $"Remote IP: {realOrigin}";
            if (controller.Request.Headers.TryGetValue("X-Forwarded-For", out StringValues forwardHeader))
            {
                sender += ", X-Forwarded-For: " + string.Join(" / ", forwardHeader);
                if (PasteServer.TrustXForwardedFor && forwardHeader.Count > 0)
                {
                    realOrigin = string.Join(" / ", forwardHeader);
                }
            }
            if (controller.Request.Headers.TryGetValue("REMOTE_ADDR", out StringValues remoteAddr))
            {
                sender += ", REMOTE_ADDR: " + string.Join(" / ", remoteAddr);
            }
            if (sender.StartsWith(", "))
            {
                sender = sender[(", ".Length)..];
            }
            if (sender.Length == 0)
            {
                sender = "Unknown";
            }
            Console.WriteLine($"Attempted paste from {realOrigin} as {sender}");
            IFormCollection form = controller.Request.Form;
            if (!form.TryGetValue("pastetitle", out StringValues pasteTitle) || !form.TryGetValue("pastecontents", out StringValues pasteContents))
            {
                Console.Error.WriteLine("Refused paste: Form missing keys");
                return RejectPaste(controller, type);
            }
            if (pasteTitle.Count != 1 || pasteContents.Count != 1)
            {
                Console.Error.WriteLine("Refused paste: Improper form data");
                return RejectPaste(controller, type);
            }
            if (edits == null && form.TryGetValue("editing", out StringValues editValue) && editValue.Count == 1 && editValue[0] != "")
            {
                if (!long.TryParse(editValue[0], out long editingID))
                {
                    Console.Error.WriteLine("Refused paste: Improper form data (editing key)");
                    return RejectPaste(controller, type);
                }
                if (!PasteDatabase.TryGetPaste(editingID, out edits))
                {
                    Console.Error.WriteLine("Refused edit paste: unlisted ID");
                    return RejectPaste(controller, type);
                }
            }
            bool micro = form.TryGetValue("response", out StringValues responseValue) && responseValue.Count == 1 && responseValue[0].ToLowerFast() == "micro";
            bool microv2 = form.TryGetValue("v", out StringValues versionValue) && versionValue.Count == 1 && versionValue[0].ToLowerFast() == "200";
            if (micro)
            {
                sender += ", response=micro";
                if (microv2)
                {
                    sender += "v2";
                }
            }
            if (!PasteType.ValidPasteTypes.TryGetValue(type, out PasteType actualType))
            {
                Console.Error.WriteLine($"Refused paste: Unknown type {type}");
                return RejectPaste(controller, type);
            }
            string pasteTitleText = ForceCleanText(pasteTitle[0]);
            if (string.IsNullOrWhiteSpace(pasteTitleText))
            {
                pasteTitleText = $"Unnamed {actualType.DisplayName} Paste";
            }
            if (pasteTitleText.Length > 200)
            {
                pasteTitleText = pasteTitleText[0..200];
            }
            string pasteContentText = pasteContents[0];
            if (pasteContentText.Length > PasteServer.MaxPasteRawLength)
            {
                pasteContentText = pasteContentText[0..PasteServer.MaxPasteRawLength];
            }
            pasteContentText = pasteContentText.Replace('\0', ' ');
            if (!IsValidPaste(pasteTitleText, pasteContentText))
            {
                return RejectPaste(controller, type);
            }
            if (!RateLimiter.TryUser(realOrigin))
            {
                Console.Error.WriteLine("Refused paste: spam");
                return RejectPaste(controller, type);
            }
            Paste newPaste = new()
            {
                Title = pasteTitleText,
                Type = actualType.Name.ToLowerFast(),
                PostSourceData = sender,
                Date = StringConversionHelper.DateTimeToString(DateTimeOffset.Now, false),
                Raw = pasteContentText,
                Formatted = actualType.Highlight(pasteContentText),
                Edited = (edits == null ? 0 : edits.ID)
            };
            if (newPaste.Formatted.Length > PasteServer.MaxPasteRawLength * 5)
            {
                Console.Error.WriteLine("Refused paste: Massive formatted-content length");
                return RejectPaste(controller, type);
            }
            if (newPaste.Formatted == null)
            {
                Console.Error.WriteLine("Refused paste: format failed");
                return RejectPaste(controller, type);
            }
            string diffText = null;
            if (edits != null)
            {
                diffText = DiffHighlighter.GenerateDiff(edits.Raw, newPaste.Raw, out bool hasDifferences);
                if (!hasDifferences)
                {
                    Console.Error.WriteLine("Refused paste: edits nothing");
                    return RejectPaste(controller, type);
                }
            }
            newPaste.ID = PasteDatabase.GetNextPasteID();
            if (diffText != null)
            {
                Paste diffPaste = new()
                {
                    Title = $"Diff Report Between Paste #{newPaste.ID} and #{edits.ID}",
                    Type = "diff",
                    PostSourceData = "(GENERATED), " + sender,
                    Date = StringConversionHelper.DateTimeToString(DateTimeOffset.Now, false),
                    Raw = diffText,
                    Formatted = DiffHighlighter.Highlight(diffText),
                    Edited = newPaste.ID,
                    ID = PasteDatabase.GetNextPasteID()
                };
                newPaste.DiffReport = diffPaste.ID;
                PasteDatabase.SubmitPaste(diffPaste);
                PasteServer.RunNewPasteWebhook(diffPaste);
            }
            PasteDatabase.SubmitPaste(newPaste);
            PasteServer.RunNewPasteWebhook(newPaste);
            Console.Error.WriteLine($"Accepted new paste: {newPaste.ID} from {newPaste.PostSourceData}");
            if (micro)
            {
                controller.Response.ContentType = "text/plain";
                return controller.Ok(microv2 ? $"{PasteServer.URL_BASE}/View/{newPaste.ID}\n" : $"/paste/{newPaste.ID}\n");
            }
            return controller.Redirect($"/View/{newPaste.ID}");
        }

        public static bool IsValidPaste(string title, string content)
        {
            if (content.Length < 100)
            {
                Console.Error.WriteLine($"Refused paste: too-short content {content.Length}");
                return false;
            }
            if (content.Length < 1024)
            {
                int nonEmptyLines = content.SplitFast('\n').Select(s => s.Trim().Length > 5).Count();
                if (nonEmptyLines < 3)
                {
                    Console.Error.WriteLine($"Refused paste: too-few lines {nonEmptyLines} (c.Len = {content.Length})");
                    return false;
                }
            }
            string titleLow = title.ToLowerFast();
            if (titleLow.Contains("<a href="))
            {
                Console.Error.WriteLine("Refused paste: spam-bot title");
                return false;
            }
            foreach (string block in PasteServer.SpamBlockPartialTitles)
            {
                if (titleLow.Contains(block))
                {
                    Console.Error.WriteLine("Refused paste: spam-block-partial-titles in title");
                    return false;
                }
            }
            foreach (string block in PasteServer.SpamBlockTitles)
            {
                if (titleLow == block)
                {
                    Console.Error.WriteLine("Refused paste: spam-block-titles in title");
                    return false;
                }
            }
            if (PasteServer.SpamBlockKeywords.Length > 0)
            {
                string contentLow = content.ToLowerFast();
                foreach (string block in PasteServer.SpamBlockKeywords)
                {
                    if (titleLow.Contains(block))
                    {
                        Console.Error.WriteLine("Refused paste: spam-block-keyphrase in title");
                        return false;
                    }
                    if (contentLow.Contains(block))
                    {
                        Console.Error.WriteLine("Refused paste: spam-block-keyphrase in paste content");
                        return false;
                    }
                }
            }
            if (content.Length > 100 * 1024)
            {
                int newLines = content.CountCharacter('\n');
                if (newLines < 20)
                {
                    Console.Error.WriteLine($"Refused paste: massive paste with too few lines {newLines} (c.Len = {content.Length})");
                    return false;
                }
                return true;
            }
            string[] lines = content.SplitFast('\n');
            int linkLines = 0;
            int normalLines = 0;
            foreach (string line in lines)
            {
                if (line.Trim().Length < 5)
                {
                    continue;
                }
                if (line.Contains("http"))
                {
                    linkLines++;
                }
                else
                {
                    normalLines++;
                }
            }
            if (linkLines >= normalLines || (linkLines > 0 && normalLines < 4))
            {
                Console.Error.WriteLine($"Refused paste: link spambot? {linkLines} linkLines and {normalLines} normal lines");
                return false;
            }
            if (normalLines < 20 && PasteServer.SpamBlockKeywords.Length > 0)
            {
                string contentLow = content.ToLowerFast();
                foreach (string block in PasteServer.SpamBlockShortKeywords)
                {
                    if (titleLow.Contains(block))
                    {
                        Console.Error.WriteLine("Refused paste: spam-block-short-keyphrase in title");
                        return false;
                    }
                    if (contentLow.Contains(block))
                    {
                        Console.Error.WriteLine("Refused paste: spam-block-short-keyphrase in paste content");
                        return false;
                    }
                }
            }
            return true;
        }

        public IActionResult Index()
        {
            Setup();
            if (Request.Method == "POST")
            {
                return HandlePost(this, "script");
            }
            return View(new NewPasteModel() { NewType = "script" });
        }

        public IActionResult Script()
        {
            Setup();
            if (Request.Method == "POST")
            {
                return HandlePost(this, "script");
            }
            return View("Index", new NewPasteModel() { NewType = "script" });
        }

        public IActionResult Log()
        {
            Setup();
            if (Request.Method == "POST")
            {
                return HandlePost(this, "log");
            }
            return View("Index", new NewPasteModel() { NewType = "log" });
        }

        public IActionResult BBCode()
        {
            Setup();
            if (Request.Method == "POST")
            {
                return HandlePost(this, "bbcode");
            }
            return View("Index", new NewPasteModel() { NewType = "bbcode" });
        }

        public IActionResult Text()
        {
            Setup();
            if (Request.Method == "POST")
            {
                return HandlePost(this, "text");
            }
            return View("Index", new NewPasteModel() { NewType = "text" });
        }

        public IActionResult Other()
        {
            Setup();
            string otherType = "csharp";
            if (Request.Query.TryGetValue("selected", out StringValues selectionValues) && selectionValues.Any() && PasteType.ValidPasteTypes.ContainsKey(selectionValues[0]))
            {
                otherType = selectionValues[0];
            }
            if (Request.Method == "POST")
            {
                return HandlePost(this, otherType);
            }
            return View("Index", new NewPasteModel() { NewType = "other" });
        }

        public IActionResult Edit()
        {
            Setup();
            if (!Request.HttpContext.Items.TryGetValue("viewable", out object pasteIdObject) || pasteIdObject is not string pasteIdText)
            {
                Console.Error.WriteLine("Refused edit: ID missing");
                return Redirect("/");
            }
            if (!long.TryParse(pasteIdText, out long pasteId))
            {
                Console.Error.WriteLine("Refused edit: non-numeric ID");
                return Redirect("/Error/Error404");
            }
            if (!PasteDatabase.TryGetPaste(pasteId, out Paste paste))
            {
                Console.Error.WriteLine("Refused view: unlisted ID");
                return Redirect("/Error/Error404");
            }
            if (Request.Method != "POST")
            {
                return Redirect($"/View/{paste.ID}");
            }
            if (Request.Form.TryGetValue("button_type", out StringValues buttonTypeVal) && buttonTypeVal.Count == 1)
            {
                switch (buttonTypeVal[0])
                {
                    case "edit":
                        return View("Index", new NewPasteModel() { NewType = paste.Type, Edit = paste });
                    case "spamblock":
                        if ((bool)ViewData["auth_isloggedin"] && paste.HistoricalContent is null)
                        {
                            paste.Type = "text";
                            paste.Title = "REMOVED SPAM POST";
                            if (string.IsNullOrWhiteSpace(paste.HistoricalContent))
                            {
                                paste.HistoricalContent = paste.Title + "\n\n" + paste.Raw;
                            }
                            paste.Raw = "Spam post removed from view.";
                            paste.Formatted = HighlighterCore.HighlightPlainText("Spam post removed from view.");
                            PasteDatabase.SubmitPaste(paste);
                            Console.WriteLine($"paste {pasteId} removed by logged in staff - {(ulong)ViewData["auth_userid"]}");
                            return Redirect($"/View/{paste.ID}");
                        }
                        break;
                    case "rerender":
                        if ((bool)ViewData["auth_isloggedin"] && paste.HistoricalContent is null)
                        {
                            try
                            {
                                Console.WriteLine($"Rerender paste {paste.ID} on behalf of staff {(ulong)ViewData["auth_userid"]}");
                                PasteDatabase.FillPaste(paste);
                                string origFormat = paste.Formatted;
                                if (PasteType.ValidPasteTypes.TryGetValue(paste.Type, out PasteType type))
                                {
                                    paste.Formatted = type.Highlight(paste.Raw);
                                    if (origFormat.TrimEnd() != paste.Formatted.TrimEnd())
                                    {
                                        Console.WriteLine($"Updating paste {paste.ID} (was {origFormat.Length} now {paste.Formatted.Length})...");
                                        PasteDatabase.SubmitPaste(paste);
                                    }
                                    return Redirect($"/View/{paste.ID}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to rerender paste {paste.ID}: {ex}");
                            }
                        }
                        break;
                }
            }
            return HandlePost(this, paste.Type, paste);
        }
    }
}
