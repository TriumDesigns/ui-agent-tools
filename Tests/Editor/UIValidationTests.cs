// UIAgentTools - Unity Test Framework wrapper around UIValidator, for CI.
// One test case per UI prefab and scene; fails only on errors that are not
// in UIAgentMap/validation-baseline.json. (`ui-agent validate` runs the same
// checks and works while the Editor is open; use this for CI pipelines.)
//
//   Unity -batchmode -projectPath . -runTests -testPlatform EditMode \
//         -assemblyNames UIAgentTools.Tests.Editor -testResults results.xml
//   (do NOT add -quit to a -runTests command)

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UIAgentTools.Tests
{
    public class UIValidationTests
    {
        private const string NoTargets = "(no UI prefabs or scenes found)";

        public static IEnumerable<string> Targets()
        {
            var list = UIMapper.FindAllTargets(UIAgentConfig.Load());
            if (list.Count == 0) list.Add(NoTargets);
            return list;
        }

        [Test, TestCaseSource(nameof(Targets))]
        public void HasNoNewValidationErrors(string assetPath)
        {
            if (assetPath == NoTargets) Assert.Ignore(NoTargets);
            var accepted = new HashSet<string>(UIValidator.LoadBaseline().acceptedErrorKeys);
            var newErrors = UIValidator.ValidateAsset(assetPath).Where(i => i.IsError && !accepted.Contains(i.Key)).ToList();
            Assert.IsEmpty(newErrors, string.Join("\n", newErrors.Select(i => "[" + i.check + "] " + i.address + " - " + i.message).ToArray()));
        }
    }
}
