﻿using StableDiffusionGui.Data;
using StableDiffusionGui.Installation;
using StableDiffusionGui.Io;
using StableDiffusionGui.Main;
using StableDiffusionGui.MiscUtils;
using StableDiffusionGui.Os;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StableDiffusionGui.Implementations
{
    internal class InvokeAiUtils
    {
        public static string ModelsYamlPath { get { return Path.Combine(Paths.GetDataPath(), Constants.Dirs.SdRepo, "configs", "models.yaml"); } }

        public static void WriteModelsYaml(string mdlName, string vaeName = "", string keyName = "default")
        {
            var mdl = Paths.GetModel(mdlName, false, Enums.StableDiffusion.ModelType.Normal);
            var vae = Paths.GetModel(vaeName, false, Enums.StableDiffusion.ModelType.Vae);
            WriteModelsYaml(mdl, vae, keyName);
        }

        public static void WriteModelsYaml(Model mdl, Model vae, string keyName = "default")
        {
            string text = $"{keyName}:\n" +
                $"    config: configs/stable-diffusion/v1-inference.yaml\n" +
                $"    weights: {(mdl == null ? $"unknown{Constants.FileExts.ValidSdModels.First()}" : mdl.FullName.Wrap(true))}\n" +
                $"    {(vae != null && File.Exists(vae.FullName) ? $"vae: {vae.FullName.Wrap(true)}" : "")}\n" +
                $"    description: Current NMKD SD GUI model\n" +
                $"    width: 512\n" +
                $"    height: 512\n" +
                $"    default: true";

            File.WriteAllText(ModelsYamlPath, text);
        }

        /// <summary> Writes all models into models.yml for InvokeAI to use </summary>
        public static async Task WriteModelsYamlAll(Model selectedMdl, Model selectedVae, List<Model> cachedModels = null, List<Model> cachedModelsVae = null)
        {
            if (cachedModels == null || cachedModels.Count < 1)
                cachedModels = Paths.GetModels(Enums.StableDiffusion.ModelType.Normal);

            if (cachedModelsVae == null || cachedModelsVae.Count < 1)
                cachedModelsVae = Paths.GetModels(Enums.StableDiffusion.ModelType.Vae);

            if (!Config.GetBool("disablePickleScanner"))
            {
                Logger.Log($"Preparing model files...");

                var pickleScanResults = await TtiUtils.VerifyModelsWithPseudoHash(cachedModels.Concat(cachedModelsVae));
                var cachedModelsUnsafe = cachedModels.Concat(cachedModelsVae).Where(m => !pickleScanResults[IoUtils.GetPseudoHash(m.FullName)]).ToList();

                cachedModels = cachedModels.Except(cachedModelsUnsafe).ToList();
                cachedModelsVae = cachedModelsVae.Except(cachedModelsUnsafe).ToList();

                if (cachedModelsUnsafe.Any())
                {
                    Logger.Log($"Warning: The following model files were disabled because they might be malicious:\n" +
                        $"{string.Join("\n", cachedModelsUnsafe.Select(model => model.Name))}");

                    if (cachedModelsUnsafe.Select(m => m.FullName).Contains(selectedMdl.FullName))
                        TextToImage.Cancel("Selected model appears to contain malware.");
                }
            }
            
            string text = "";

            cachedModelsVae.Insert(0, null); // Insert null entry, for looping

            foreach (Model mdl in cachedModels)
            {
                bool inpaint = mdl.Name.MatchesWildcard("*-inpainting.*");

                foreach (Model vae in cachedModelsVae)
                {
                    if (inpaint && !EnabledFeatures.RunwayMlInpainting)
                        continue;

                    string configFile = File.Exists(mdl.FullName + ".yaml") ? (mdl.FullName + ".yaml").Wrap(true) : $"configs/stable-diffusion/{(inpaint ? "v1-inpainting-inference" : "v1-inference")}.yaml";

                    text += $"{GetMdlNameForYaml(mdl, vae)}:\n" +
                    $"    config: {configFile}\n" +
                    $"    weights: {mdl.FullName.Wrap(true)}\n" +
                    $"{(vae != null && File.Exists(vae.FullName) ? $"    vae: {vae.FullName.Wrap(true)}\n" : "")}" +
                    $"    description: {mdl.Name}\n" +
                    $"    width: 512\n" +
                    $"    height: 512\n" +
                    $"    default: {IsModelDefault(mdl, vae, selectedMdl, selectedVae).ToString().Lower()}\n\n";
                }
            }

            File.WriteAllText(ModelsYamlPath, text);
        }

        private static bool IsModelDefault(Model mdl, Model vae, Model selectedMdl, Model selectedVae)
        {
            if (mdl == null || selectedMdl == null)
                return false;

            bool mdlMatch = mdl.FullName == selectedMdl.FullName;
            bool vaeMatch;

            if (selectedVae == null)
                vaeMatch = vae == null;
            else
                vaeMatch = vae != null && selectedVae.FullName == vae.FullName;

            return mdlMatch && vaeMatch;
        }

        public static string GetMdlNameForYaml(Model mdl, Model vae)
        {
            return $"{mdl.Name}{(vae == null ? "-noVae" : $"-{vae.Name}")}";
        }

        public static string GetModelsYamlHash(IoUtils.Hash hashType = IoUtils.Hash.CRC32, bool ignoreDefaultKey = true)
        {
            var lines = File.ReadAllLines(ModelsYamlPath);

            if (ignoreDefaultKey)
                lines = lines.Where(l => !l.Contains("    default: ")).ToArray();

            string contentStr = string.Join(Environment.NewLine, lines);
            return IoUtils.GetHash(contentStr, hashType, false);
        }

        public static string ConvertOldAttentionSyntax(string prompt)
        {
            if (!prompt.Contains("(") && !prompt.Contains("{")) // Skip if no parentheses/curly brackets were used
                return prompt;

            if (prompt.Contains(")+") || prompt.Contains(")-") || Regex.Matches(prompt, @"\)\d.\d+").Count >= 1) // Assume new syntax is already used
                return prompt;

            prompt = prompt.Replace("\\(", "escapedParenthesisOpen").Replace("\\)", "escapedParenthesisClose");

            var parentheses = Regex.Matches(prompt, @"\(((?>[^()]+|\((?<n>)|\)(?<-n>))+(?(n)(?!)))\)");

            for (int i = 0; i < parentheses.Count; i++)
            {
                string match = parentheses[i].Value;
                int count = match.Where(c => c == ')').Count();
                string converted = $"({match.Remove("(").Remove(")")}){new string('+', count)}";
                prompt = prompt.Replace(match, converted);
            }

            var curlyBrackets = Regex.Matches(prompt, @"\{((?>[^{}]+|\{(?<n>)|\}(?<-n>))+(?(n)(?!)))\}");

            for (int i = 0; i < curlyBrackets.Count; i++)
            {
                string match = curlyBrackets[i].Value;
                int count = match.Where(c => c == '}').Count();
                string converted = $"({match.Remove("{").Remove("}")}){new string('-', count)}";
                prompt = prompt.Replace(match, converted);
            }

            prompt = prompt.Replace("escapedParenthesisOpen", "\\(").Replace("escapedParenthesisClose", "\\)");

            return prompt;
        }
    }
}