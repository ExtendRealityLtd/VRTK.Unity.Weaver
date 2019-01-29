﻿namespace Malimbe.PropertySetterMethod.Fody
{
    using System.Collections.Generic;
    using System.Linq;
    using global::Fody;
    using Malimbe.Shared;
    using Mono.Cecil;
    using Mono.Cecil.Cil;
    using Mono.Collections.Generic;

    // ReSharper disable once UnusedMember.Global
    public sealed class ModuleWeaver : BaseModuleWeaver
    {
        private static readonly string _fullAttributeName = typeof(CalledBySetterAttribute).FullName;

        public override bool ShouldCleanReference =>
            true;

        public override void Execute()
        {
            IEnumerable<MethodDefinition> methodDefinitions =
                ModuleDefinition.Types.SelectMany(definition => definition.Methods);
            foreach (MethodDefinition methodDefinition in methodDefinitions)
            {
                if (!FindAndRemoveAttribute(methodDefinition, out CustomAttribute attribute)
                    || !FindProperty(methodDefinition, attribute, out PropertyDefinition propertyDefinition))
                {
                    continue;
                }

                if (propertyDefinition.GetMethod == null)
                {
                    LogError(
                        $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                        + $" property '{propertyDefinition.FullName}' but the property has no getter.");
                    continue;
                }

                if (propertyDefinition.SetMethod == null)
                {
                    LogError(
                        $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                        + $" property '{propertyDefinition.FullName}' but the property has no setter.");
                    continue;
                }

                InsertSetMethodCallIntoPropertySetter(propertyDefinition, methodDefinition);
            }
        }

        public override IEnumerable<string> GetAssembliesForScanning()
        {
            yield break;
        }

        private bool FindAndRemoveAttribute(IMemberDefinition methodDefinition, out CustomAttribute foundAttribute)
        {
            foundAttribute = methodDefinition.CustomAttributes.SingleOrDefault(
                attribute => attribute.AttributeType.FullName == _fullAttributeName);
            if (foundAttribute == null)
            {
                return false;
            }

            methodDefinition.CustomAttributes.Remove(foundAttribute);
            LogInfo($"Removed the attribute '{_fullAttributeName}' from the method '{methodDefinition.FullName}'.");
            return true;
        }

        private bool FindProperty(
            MethodDefinition methodDefinition,
            ICustomAttribute attribute,
            out PropertyDefinition propertyDefinition)
        {
            string propertyName = (string)attribute.ConstructorArguments.Single().Value;

            propertyDefinition =
                methodDefinition.DeclaringType.Properties?.SingleOrDefault(
                    definition => definition.Name == propertyName);
            if (propertyDefinition == null)
            {
                LogError(
                    $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                    + $" property '{propertyName}' but the property doesn't exist.");
                return false;
            }

            string expectedTypeFullName = propertyDefinition.PropertyType.FullName;
            if (methodDefinition.ReturnType.FullName == TypeSystem.VoidReference.FullName
                && methodDefinition.Parameters?.Count == 2
                && methodDefinition.Parameters[0].ParameterType.FullName == expectedTypeFullName
                && methodDefinition.Parameters[1].ParameterType.IsByReference
                && methodDefinition.Parameters[1].ParameterType.FullName.TrimEnd('&') == expectedTypeFullName)
            {
                return true;
            }

            LogError(
                $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                + $" property '{propertyName}' but the method signature doesn't match. The expected signature is"
                + $" 'void {methodDefinition.Name}({expectedTypeFullName}, ref {expectedTypeFullName})'.");
            propertyDefinition = null;

            return false;
        }

        private void InsertSetMethodCallIntoPropertySetter(
            PropertyDefinition propertyDefinition,
            MethodReference setMethodReference)
        {
            MethodBody methodBody = propertyDefinition.SetMethod.Body;
            Collection<Instruction> instructions = methodBody.Instructions;
            int index = -1;

            MethodReference getMethodReference = propertyDefinition.GetMethod.GetGeneric();

            // previousValue = this.property;
            VariableDefinition previousValueVariableDefinition =
                new VariableDefinition(propertyDefinition.PropertyType);
            methodBody.Variables.Add(previousValueVariableDefinition);

            // Load this (for getter call)
            instructions.Insert(++index, Instruction.Create(OpCodes.Ldarg_0));
            // Call getter
            instructions.Insert(++index, Instruction.Create(OpCodes.Callvirt, getMethodReference));
            // Store into previousValue
            instructions.Insert(++index, Instruction.Create(OpCodes.Stloc, previousValueVariableDefinition));

            List<Instruction> returnInstructions =
                instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToList();
            foreach (Instruction returnInstruction in returnInstructions)
            {
                index = instructions.IndexOf(returnInstruction) - 1;

                // this.setMethod(previousValue, this.propertyBackingField);

                // Load this (for setMethod call)
                instructions.Insert(++index, Instruction.Create(OpCodes.Ldarg_0));
                // Load previousValue
                instructions.Insert(++index, Instruction.Create(OpCodes.Ldloc, previousValueVariableDefinition));
                // Load this (for backing field get)
                instructions.Insert(++index, Instruction.Create(OpCodes.Ldarg_0));
                // Load address of backing field
                instructions.Insert(++index, Instruction.Create(OpCodes.Ldflda, propertyDefinition.GetBackingField()));
                // Call setMethod
                instructions.Insert(++index, Instruction.Create(OpCodes.Callvirt, setMethodReference.GetGeneric()));
            }

            LogInfo(
                $"Inserted a call to the method '{setMethodReference.FullName}' into"
                + $" the setter of the property '{propertyDefinition.FullName}'.");
        }
    }
}
