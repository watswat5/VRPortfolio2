using System;
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;

namespace WifiHeatmap.Core.Tests
{
    public sealed class ProbeCalibrationTests
    {
        private string folder;
        private string Pathname => Path.Combine(folder, "offset.json");
        private static ProbeCalibration Data() => new ProbeCalibration
        {
            MountId = "left-pod", Controller = "LTouch", HeadsetModel = "Quest3", CalibrationId = "manual-test",
            SavedUtc = DateTime.UtcNow, PodPosition = new[] { .002f, .017f, .058f },
            PodRotation = new[] { 0f, .70710677f, 0f, .70710677f },
            AntennaPosition = new[] { .001f, .011f, 0f }, AntennaRotation = new[] { 0f, 0f, 0f, 1f },
        };
        [SetUp] public void SetUp() { folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TearDown] public void TearDown() { Directory.Delete(folder, true); }

        [Test] public void TransformsAndIdentitySurviveSaveLoadAndReplacement()
        {
            ProbeCalibrationFile.Save(Pathname, Data());
            var second = Data(); second.PodPosition[0] = .012f; second.CalibrationId = "manual-second";
            ProbeCalibrationFile.Save(Pathname, second);
            var loaded = ProbeCalibrationFile.Load(Pathname, "left-pod", "LTouch", "Quest3");
            CollectionAssert.AreEqual(second.PodPosition, loaded.PodPosition);
            CollectionAssert.AreEqual(second.PodRotation, loaded.PodRotation);
            CollectionAssert.AreEqual(second.AntennaPosition, loaded.AntennaPosition);
            CollectionAssert.AreEqual(second.AntennaRotation, loaded.AntennaRotation);
            Assert.AreEqual("manual-second", loaded.CalibrationId);
            Assert.IsFalse(File.Exists(Pathname + ".tmp"));
        }

        [TestCase("other", "LTouch", "Quest3")]
        [TestCase("left-pod", "RTouch", "Quest3")]
        [TestCase("left-pod", "LTouch", "Quest3S")]
        public void DifferentHardwareIsRejected(string mount, string controller, string model)
        {
            ProbeCalibrationFile.Save(Pathname, Data());
            Assert.Throws<InvalidDataException>(() => ProbeCalibrationFile.Load(Pathname, mount, controller, model));
        }

        [Test] public void InvalidSavePreservesPreviousFile()
        {
            ProbeCalibrationFile.Save(Pathname, Data());
            string before = File.ReadAllText(Pathname);
            var invalid = Data(); invalid.PodRotation = new[] { 0f, 0f, 0f, 0f };
            Assert.Throws<InvalidDataException>(() => ProbeCalibrationFile.Save(Pathname, invalid));
            Assert.AreEqual(before, File.ReadAllText(Pathname));
        }

        [Test] public void MissingCorruptOversizedOrIncompleteFileFails()
        {
            Assert.Throws<FileNotFoundException>(() => ProbeCalibrationFile.Load(Pathname, "left-pod", "LTouch", "Quest3"));
            File.WriteAllText(Pathname, "{");
            Assert.Throws<JsonSerializationException>(() => ProbeCalibrationFile.Load(Pathname, "left-pod", "LTouch", "Quest3"));
            File.WriteAllText(Pathname, "{}");
            Assert.Throws<JsonSerializationException>(() => ProbeCalibrationFile.Load(Pathname, "left-pod", "LTouch", "Quest3"));
            File.WriteAllText(Pathname, new string(' ', ProbeCalibrationFile.MaximumBytes + 1));
            Assert.Throws<InvalidDataException>(() => ProbeCalibrationFile.Load(Pathname, "left-pod", "LTouch", "Quest3"));
        }

        [Test] public void FutureVersionsInvalidNumbersAndLargeOffsetsAreRejected()
        {
            var data = Data(); data.Version = 2;
            Assert.Throws<InvalidDataException>(() => data.Validate("left-pod", "LTouch", "Quest3"));
            data = Data(); data.PodPosition[0] = float.NaN;
            Assert.Throws<InvalidDataException>(() => data.Validate("left-pod", "LTouch", "Quest3"));
            data = Data(); data.AntennaPosition[1] = .6f;
            Assert.Throws<InvalidDataException>(() => data.Validate("left-pod", "LTouch", "Quest3"));
        }

        [Test] public void MirrorMapsLeftMountOntoRightAndBack()
        {
            float[] p = { .002f, .017f, .058f }, q = { .1f, .7f, -.2f, .677f };
            CollectionAssert.AreEqual(new[] { -.002f, .017f, .058f }, ProbeMirror.Position(p));
            CollectionAssert.AreEqual(p, ProbeMirror.Position(ProbeMirror.Position(p)));
            CollectionAssert.AreEqual(q, ProbeMirror.Rotation(ProbeMirror.Rotation(q)));
            // A mirrored rotation turns a mirrored point onto the mirror of the original's result.
            var rotation = System.Numerics.Quaternion.Normalize(new System.Numerics.Quaternion(q[0], q[1], q[2], q[3]));
            float[] m = ProbeMirror.Rotation(new[] { rotation.X, rotation.Y, rotation.Z, rotation.W });
            var mirrored = new System.Numerics.Quaternion(m[0], m[1], m[2], m[3]);
            var point = new System.Numerics.Vector3(.03f, -.01f, .02f);
            var expected = System.Numerics.Vector3.Transform(point, rotation);
            var actual = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(-point.X, point.Y, point.Z), mirrored);
            Assert.That(actual.X, Is.EqualTo(-expected.X).Within(1e-6f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1e-6f));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(1e-6f));
        }
    }
}
