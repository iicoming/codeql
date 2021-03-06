using System;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Semmle.Util;

namespace Semmle.Extraction.CIL.Entities
{
    /// <summary>
    /// A constructed type.
    /// </summary>
    public sealed class ConstructedType : Type
    {
        private readonly Type unboundGenericType;

        // Either null or notEmpty
        private readonly Type[]? thisTypeArguments;

        public override IEnumerable<Type> ThisTypeArguments => thisTypeArguments.EnumerateNull();

        public override IEnumerable<Type> ThisGenericArguments => thisTypeArguments.EnumerateNull();

        public override IEnumerable<IExtractionProduct> Contents
        {
            get
            {
                foreach (var c in base.Contents)
                    yield return c;

                var i = 0;
                foreach (var type in ThisGenericArguments)
                {
                    yield return type;
                    yield return Tuples.cil_type_argument(this, i++, type);
                }
            }
        }

        public override Type SourceDeclaration => unboundGenericType;

        public ConstructedType(Context cx, Type unboundType, IEnumerable<Type> typeArguments) : base(cx)
        {
            var suppliedArgs = typeArguments.Count();
            if (suppliedArgs != unboundType.TotalTypeParametersCheck)
                throw new InternalError("Unexpected number of type arguments in ConstructedType");

            unboundGenericType = unboundType;
            var thisParams = unboundType.ThisTypeParameters;

            if (typeArguments.Count() == thisParams)
            {
                containingType = unboundType.ContainingType;
                thisTypeArguments = typeArguments.ToArray();
            }
            else if (thisParams == 0)
            {
                // all type arguments belong to containing type
                containingType = unboundType.ContainingType!.Construct(typeArguments);
            }
            else
            {
                // some type arguments belong to containing type
                var parentParams = suppliedArgs - thisParams;
                containingType = unboundType.ContainingType!.Construct(typeArguments.Take(parentParams));
                thisTypeArguments = typeArguments.Skip(parentParams).ToArray();
            }
        }

        public override bool Equals(object? obj)
        {
            if (obj is ConstructedType t && Equals(unboundGenericType, t.unboundGenericType) && Equals(containingType, t.containingType))
            {
                if (thisTypeArguments is null)
                    return t.thisTypeArguments is null;
                if (!(t.thisTypeArguments is null))
                    return thisTypeArguments.SequenceEqual(t.thisTypeArguments);
            }
            return false;
        }

        public override int GetHashCode()
        {
            var h = unboundGenericType.GetHashCode();
            h = 13 * h + (containingType is null ? 0 : containingType.GetHashCode());
            if (!(thisTypeArguments is null))
                h = h * 13 + thisTypeArguments.SequenceHash();
            return h;
        }

        private readonly Type? containingType;
        public override Type? ContainingType => containingType;

        public override string Name => unboundGenericType.Name;

        public override Namespace Namespace => unboundGenericType.Namespace!;

        public override int ThisTypeParameters => thisTypeArguments == null ? 0 : thisTypeArguments.Length;

        public override CilTypeKind Kind => unboundGenericType.Kind;

        public override Type Construct(IEnumerable<Type> typeArguments)
        {
            throw new NotImplementedException();
        }

        public override void WriteId(TextWriter trapFile, bool inContext)
        {
            if (ContainingType != null)
            {
                ContainingType.GetId(trapFile, inContext);
                trapFile.Write('.');
            }
            else
            {
                WriteAssemblyPrefix(trapFile);

                if (!Namespace.IsGlobalNamespace)
                {
                    Namespace.WriteId(trapFile);
                    trapFile.Write('.');
                }
            }
            trapFile.Write(unboundGenericType.Name);

            if (thisTypeArguments != null && thisTypeArguments.Any())
            {
                trapFile.Write('<');
                var index = 0;
                foreach (var t in thisTypeArguments)
                {
                    trapFile.WriteSeparator(",", ref index);
                    t.WriteId(trapFile);
                }
                trapFile.Write('>');
            }
        }

        public override void WriteAssemblyPrefix(TextWriter trapFile) => unboundGenericType.WriteAssemblyPrefix(trapFile);

        public override IEnumerable<Type> TypeParameters => GenericArguments;

        public override IEnumerable<Type> MethodParameters => throw new NotImplementedException();
    }
}
