using System;
using System.Collections.Generic;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Documents modifies dans l'IDE et pas encore enregistres. A consulter avant d'ecrire un
    /// fichier ouvert dans Visual Studio : sinon l'une des deux versions est perdue.
    /// </summary>
    internal static class DocumentTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "get_dirty_documents",
                "Documents ouverts dans Visual Studio avec des modifications non enregistrees. A appeler " +
                "avant de modifier un fichier sur disque : s'il figure ici, l'enregistrer (save_documents) " +
                "ou demander a l'utilisateur, sinon l'une des deux versions sera perdue.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("path", "Ne verifier que ce fichier. Absolu, ou relatif au repertoire de la solution.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var path = Args.Str(args, "path");
                    var resolved = string.IsNullOrEmpty(path) ? null : SolutionTools.ResolvePath(path);

                    var dirty = new JArray();
                    foreach (var document in DocumentHelper.DirtyDocuments())
                    {
                        var description = DocumentHelper.Describe(document);
                        if (resolved != null && !DocumentHelper.SamePath((string)description["path"], resolved)) continue;
                        dirty.Add(description);
                    }

                    var result = new JObject
                    {
                        ["count"] = dirty.Count,
                        ["dirty"] = dirty
                    };

                    if (resolved != null)
                    {
                        result["path"] = resolved;
                        result["open"] = DocumentHelper.FindOpen(resolved) != null;
                    }

                    return result;
                }, ct));

            registry.Add(
                "save_documents",
                "Enregistre des documents modifies dans Visual Studio. Sans 'paths', enregistre tous les " +
                "documents non enregistres.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("paths", "Fichiers a enregistrer, separes par des virgules. Absolus ou relatifs a la solution.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var targets = new List<Document>();
                    var failed = new JArray();
                    var paths = Args.Str(args, "paths");

                    if (string.IsNullOrWhiteSpace(paths))
                    {
                        targets.AddRange(DocumentHelper.DirtyDocuments());
                    }
                    else
                    {
                        foreach (var raw in paths.Split(','))
                        {
                            var name = raw.Trim();
                            if (name.Length == 0) continue;

                            var resolved = SolutionTools.ResolvePath(name);
                            var document = DocumentHelper.FindOpen(resolved);
                            if (document == null)
                            {
                                failed.Add(new JObject { ["path"] = resolved, ["error"] = "Document non ouvert dans Visual Studio." });
                                continue;
                            }
                            targets.Add(document);
                        }
                    }

                    var saved = new JArray();
                    var unchanged = new JArray();

                    foreach (var document in targets)
                    {
                        var description = DocumentHelper.Describe(document);
                        if ((bool)description["saved"])
                        {
                            unchanged.Add(description["path"]);
                            continue;
                        }

                        // Une boite modale (fichier en lecture seule, encodage) est interceptee par
                        // UiThread et remontee comme ModalDialogException.
                        try
                        {
                            document.Save();
                            saved.Add(description["path"]);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject { ["path"] = description["path"], ["error"] = ex.Message });
                        }
                    }

                    return new JObject
                    {
                        ["saved"] = saved,
                        ["unchanged"] = unchanged,
                        ["failed"] = failed
                    };
                }, ct));
        }
    }
}
