using CUE4Parse_Conversion.Animations.PSA;
using CUE4Parse_Conversion.Animations.UEFormat;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Writers;
using CUE4Parse.Utils;

namespace CUE4Parse_Conversion.Animations
{
    public class AnimExporter : ExporterBase
    {
        public readonly List<Anim> AnimSequences;

        private AnimExporter(UObject export, ExporterOptions options) : base(export, options)
        {
            AnimSequences = new List<Anim>();
        }

        private AnimExporter(ExporterOptions options, UObject export, CAnimSet animSet)
            : this(export, options)
        {
            for (var sequenceIndex = 0; sequenceIndex < animSet.Sequences.Count; sequenceIndex++)
            {
                using var Ar = new FArchiveWriter();
                string ext;
                switch (Options.AnimFormat)
                {
                    case EAnimFormat.ActorX:
                        ext = "psa";
                        new ActorXAnim(animSet, sequenceIndex, Options).Save(Ar);
                        break;
                    case EAnimFormat.UEFormat:
                        ext = "ueanim";
                        new UEAnim(export.Name, animSet, sequenceIndex, export.GetPathName(), FModelSkeletonIdentity.FromSkeleton(animSet.Skeleton), Options).Save(Ar);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Options.MeshFormat), Options.MeshFormat, null);
                }

                // use GetExportSavePath()?
                AnimSequences.Add(sequenceIndex > 0
                    ? new Anim($"{PackagePath}_SEQ{sequenceIndex}.{ext}", Ar.GetBuffer())
                    : new Anim($"{PackagePath}.{ext}", Ar.GetBuffer()));
            }
        }

        private AnimExporter(ExporterOptions options, USkeleton skeleton, UAnimSequence? animSequence = null)
            : this(options, animSequence != null ? animSequence : skeleton, skeleton.ConvertAnims(animSequence))
        {

        }

        private AnimExporter(ExporterOptions options, USkeleton skeleton, UAnimMontage? animMontage = null)
            : this(options, animMontage != null ? animMontage : skeleton, skeleton.ConvertAnims(animMontage))
        {

        }

        private AnimExporter(ExporterOptions options, USkeleton skeleton, UAnimComposite? animComposite = null)
            : this(options, animComposite != null ? animComposite : skeleton, skeleton.ConvertAnims(animComposite))
        {

        }

        public AnimExporter(UAnimSequence animSequence, ExporterOptions options) : this(options, animSequence) { }
        public AnimExporter(UAnimMontage animMontage, ExporterOptions options) : this(options, animMontage) { }
        public AnimExporter(UAnimComposite animComposite, ExporterOptions options) : this(options, animComposite) { }

        private AnimExporter(ExporterOptions options, UAnimationAsset animationAsset) : base(animationAsset, options)
        {
            AnimSequences = new List<Anim>();
            if (!animationAsset.Skeleton.TryLoad<USkeleton>(out var skeleton))
                throw new ArgumentException($"Animation asset '{animationAsset.GetPathName()}' has no resolvable target skeleton.", nameof(animationAsset));
            var targetSkeleton = FModelSkeletonIdentity.FromSkeleton(skeleton);

            var animSet = animationAsset switch
            {
                UAnimSequence sequence => skeleton.ConvertAnims(sequence),
                UAnimMontage montage => skeleton.ConvertAnims(montage),
                UAnimComposite composite => skeleton.ConvertAnims(composite),
                _ => throw new NotSupportedException($"Animation asset '{animationAsset.GetType()}' is not supported.")
            };
            for (var sequenceIndex = 0; sequenceIndex < animSet.Sequences.Count; sequenceIndex++)
            {
                using var Ar = new FArchiveWriter();
                string ext;
                switch (Options.AnimFormat)
                {
                    case EAnimFormat.ActorX:
                        ext = "psa";
                        new ActorXAnim(animSet, sequenceIndex, Options).Save(Ar);
                        break;
                    case EAnimFormat.UEFormat:
                        ext = "ueanim";
                        new UEAnim(animationAsset.Name, animSet, sequenceIndex, animationAsset.GetPathName(), targetSkeleton, Options).Save(Ar);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(Options.MeshFormat), Options.MeshFormat, null);
                }

                AnimSequences.Add(sequenceIndex > 0
                    ? new Anim($"{PackagePath}_SEQ{sequenceIndex}.{ext}", Ar.GetBuffer())
                    : new Anim($"{PackagePath}.{ext}", Ar.GetBuffer()));
            }
        }


        public override bool TryWriteToDir(DirectoryInfo baseDirectory, out string label, out string savedFilePath)
        {
            var b = false;
            label = string.Empty;
            savedFilePath = PackagePath;
            if (AnimSequences.Count == 0) return b;

            var outText = "SEQ ";
            for (var i = 0; i < AnimSequences.Count; i++)
            {
                b |= AnimSequences[i].TryWriteToDir(baseDirectory, out label, out savedFilePath);
                outText += $"{i} ";
            }

            label = outText + $"as '{savedFilePath.SubstringAfterWithLast('.')}' for '{ExportName}'";
            return b;
        }

        public override bool TryWriteToZip(out byte[] zipFile)
        {
            throw new NotImplementedException();
        }

        public override void AppendToZip()
        {
            throw new NotImplementedException();
        }
    }
}
