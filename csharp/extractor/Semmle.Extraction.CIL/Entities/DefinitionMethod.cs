using System.Reflection.Metadata;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace Semmle.Extraction.CIL.Entities
{
    /// <summary>
    /// A definition method - a method defined in the current assembly.
    /// </summary>
    internal sealed class DefinitionMethod : Method
    {
        private readonly Handle handle;
        private readonly MethodDefinition md;
        private readonly PDB.Method? methodDebugInformation;
        private readonly Type declaringType;

        private readonly string name;
        private LocalVariable[]? locals;

        public MethodImplementation? Implementation { get; private set; }

        public override IList<LocalVariable>? LocalVariables => locals;

        public DefinitionMethod(GenericContext gc, MethodDefinitionHandle handle) : base(gc)
        {
            md = Cx.MdReader.GetMethodDefinition(handle);
            this.gc = gc;
            this.handle = handle;
            name = Cx.GetString(md.Name);

            declaringType = (Type)Cx.CreateGeneric(this, md.GetDeclaringType());

            signature = md.DecodeSignature(new SignatureDecoder(), this);

            methodDebugInformation = Cx.GetMethodDebugInformation(handle);
        }

        public override bool Equals(object? obj)
        {
            return obj is DefinitionMethod method && handle.Equals(method.handle);
        }

        public override int GetHashCode() => handle.GetHashCode();

        public override bool IsStatic => !signature.Header.IsInstance;

        public override Type DeclaringType => declaringType;

        public override string Name => Cx.ShortName(md.Name);

        public override string NameLabel => name;

        /// <summary>
        /// Holds if this method has bytecode.
        /// </summary>
        public bool HasBytecode => md.ImplAttributes == MethodImplAttributes.IL && md.RelativeVirtualAddress != 0;

        public override IEnumerable<IExtractionProduct> Contents
        {
            get
            {
                if (md.GetGenericParameters().Any())
                {
                    // We need to perform a 2-phase population because some type parameters can
                    // depend on other type parameters (as a constraint).
                    genericParams = new MethodTypeParameter[md.GetGenericParameters().Count];
                    for (var i = 0; i < genericParams.Length; ++i)
                        genericParams[i] = Cx.Populate(new MethodTypeParameter(this, this, i));
                    for (var i = 0; i < genericParams.Length; ++i)
                        genericParams[i].PopulateHandle(md.GetGenericParameters()[i]);
                    foreach (var p in genericParams)
                        yield return p;
                }

                var typeSignature = md.DecodeSignature(Cx.TypeSignatureDecoder, this);

                Parameters = MakeParameters(typeSignature.ParameterTypes).ToArray();

                foreach (var c in Parameters)
                    yield return c;

                foreach (var c in PopulateFlags)
                    yield return c;

                foreach (var p in md.GetParameters().Select(h => Cx.MdReader.GetParameter(h)).Where(p => p.SequenceNumber > 0))
                {
                    var pe = Parameters[IsStatic ? p.SequenceNumber - 1 : p.SequenceNumber];
                    if (p.Attributes.HasFlag(ParameterAttributes.Out))
                        yield return Tuples.cil_parameter_out(pe);
                    if (p.Attributes.HasFlag(ParameterAttributes.In))
                        yield return Tuples.cil_parameter_in(pe);
                    Attribute.Populate(Cx, pe, p.GetCustomAttributes());
                }

                yield return Tuples.metadata_handle(this, Cx.Assembly, MetadataTokens.GetToken(handle));
                yield return Tuples.cil_method(this, Name, declaringType, typeSignature.ReturnType);
                yield return Tuples.cil_method_source_declaration(this, this);
                yield return Tuples.cil_method_location(this, Cx.Assembly);

                if (HasBytecode)
                {
                    Implementation = new MethodImplementation(this);
                    yield return Implementation;

                    var body = Cx.PeReader.GetMethodBody(md.RelativeVirtualAddress);

                    if (!body.LocalSignature.IsNil)
                    {
                        var locals = Cx.MdReader.GetStandaloneSignature(body.LocalSignature);
                        var localVariableTypes = locals.DecodeLocalSignature(Cx.TypeSignatureDecoder, this);

                        this.locals = new LocalVariable[localVariableTypes.Length];

                        for (var l = 0; l < this.locals.Length; ++l)
                        {
                            this.locals[l] = Cx.Populate(new LocalVariable(Cx, Implementation, l, localVariableTypes[l]));
                            yield return this.locals[l];
                        }
                    }

                    var jump_table = new Dictionary<int, Instruction>();

                    foreach (var c in Decode(body.GetILBytes(), jump_table))
                        yield return c;

                    var filter_index = 0;
                    foreach (var region in body.ExceptionRegions)
                    {
                        yield return new ExceptionRegion(this, Implementation, filter_index++, region, jump_table);
                    }

                    yield return Tuples.cil_method_stack_size(Implementation, body.MaxStack);

                    if (methodDebugInformation != null)
                    {
                        var sourceLocation = Cx.CreateSourceLocation(methodDebugInformation.Location);
                        yield return sourceLocation;
                        yield return Tuples.cil_method_location(this, sourceLocation);
                    }
                }

                // Flags

                if (md.Attributes.HasFlag(MethodAttributes.Private))
                    yield return Tuples.cil_private(this);

                if (md.Attributes.HasFlag(MethodAttributes.Public))
                    yield return Tuples.cil_public(this);

                if (md.Attributes.HasFlag(MethodAttributes.Family))
                    yield return Tuples.cil_protected(this);

                if (md.Attributes.HasFlag(MethodAttributes.Final))
                    yield return Tuples.cil_sealed(this);

                if (md.Attributes.HasFlag(MethodAttributes.Virtual))
                    yield return Tuples.cil_virtual(this);

                if (md.Attributes.HasFlag(MethodAttributes.Abstract))
                    yield return Tuples.cil_abstract(this);

                if (md.Attributes.HasFlag(MethodAttributes.HasSecurity))
                    yield return Tuples.cil_security(this);

                if (md.Attributes.HasFlag(MethodAttributes.RequireSecObject))
                    yield return Tuples.cil_requiresecobject(this);

                if (md.Attributes.HasFlag(MethodAttributes.SpecialName))
                    yield return Tuples.cil_specialname(this);

                if (md.Attributes.HasFlag(MethodAttributes.NewSlot))
                    yield return Tuples.cil_newslot(this);

                // Populate attributes
                Attribute.Populate(Cx, this, md.GetCustomAttributes());
            }
        }

        private IEnumerable<IExtractionProduct> Decode(byte[] ilbytes, Dictionary<int, Instruction> jump_table)
        {
            // Sequence points are stored in order of offset.
            // We use an enumerator to locate the correct sequence point for each instruction.
            // The sequence point gives the location of each instruction.
            // The location of an instruction is given by the sequence point *after* the
            // instruction.
            IEnumerator<PDB.SequencePoint>? nextSequencePoint = null;
            PdbSourceLocation? instructionLocation = null;

            if (methodDebugInformation != null)
            {
                nextSequencePoint = methodDebugInformation.SequencePoints.GetEnumerator();
                if (nextSequencePoint.MoveNext())
                {
                    instructionLocation = Cx.CreateSourceLocation(nextSequencePoint.Current.Location);
                    yield return instructionLocation;
                }
                else
                {
                    nextSequencePoint = null;
                }
            }

            var child = 0;
            for (var offset = 0; offset < ilbytes.Length;)
            {
                var instruction = new Instruction(Cx, this, ilbytes, offset, child++);
                yield return instruction;

                if (nextSequencePoint != null && offset >= nextSequencePoint.Current.Offset)
                {
                    instructionLocation = Cx.CreateSourceLocation(nextSequencePoint.Current.Location);
                    yield return instructionLocation;
                    if (!nextSequencePoint.MoveNext())
                        nextSequencePoint = null;
                }

                if (instructionLocation != null)
                    yield return Tuples.cil_instruction_location(instruction, instructionLocation);

                jump_table.Add(instruction.Offset, instruction);
                offset += instruction.Width;
            }

            foreach (var i in jump_table)
            {
                foreach (var t in i.Value.JumpContents(jump_table))
                    yield return t;
            }
        }

        /// <summary>
        /// Display the instructions in the method in the debugger.
        /// This is only used for debugging, not in the code itself.
        /// </summary>
        public IEnumerable<Instruction> DebugInstructions
        {
            get
            {
                if (md.ImplAttributes == MethodImplAttributes.IL && md.RelativeVirtualAddress != 0)
                {
                    var body = Cx.PeReader.GetMethodBody(md.RelativeVirtualAddress);

                    var ilbytes = body.GetILBytes();

                    var child = 0;
                    for (var offset = 0; offset < ilbytes.Length;)
                    {
                        Instruction decoded;
                        try
                        {
                            decoded = new Instruction(Cx, this, ilbytes, offset, child++);
                            offset += decoded.Width;
                        }
                        catch  // lgtm[cs/catch-of-all-exceptions]
                        {
                            yield break;
                        }
                        yield return decoded;
                    }
                }
            }
        }
    }
}
