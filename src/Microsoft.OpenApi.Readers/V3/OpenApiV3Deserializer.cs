﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. 

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Exceptions;
using Microsoft.OpenApi.Expressions;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers.ParseNodes;

namespace Microsoft.OpenApi.Readers.V3
{
    /// <summary>
    /// Class containing logic to deserialize Open API V3 document into
    /// runtime Open API object model.
    /// </summary>
    internal static partial class OpenApiV3Deserializer
    {
        private static void ParseMap<T>(
            MapNode mapNode,
            T domainObject,
            FixedFieldMap<T> fixedFieldMap,
            PatternFieldMap<T> patternFieldMap)
        {
            if (mapNode == null)
            {
                return;
            }

            foreach (var propertyNode in mapNode)
            {
                propertyNode.ParseField(domainObject, fixedFieldMap, patternFieldMap);
            }

        }

        private static void ProcessAnyFields<T>(
            MapNode mapNode,
            T domainObject,
            AnyFieldMap<T> anyFieldMap)
        {
            foreach (var anyFieldName in anyFieldMap.Keys.ToList())
            {
                try
                {
                    mapNode.Context.StartObject(anyFieldName);

                    var value = anyFieldMap[anyFieldName].PropertyGetter(domainObject);
                    var schema = anyFieldMap[anyFieldName].SchemaGetter(domainObject);
                    if (schema?.Reference != null)
                    {
                        ScheduleAnyFieldConversion(
                            mapNode,
                            value,
                            schema,
                            v => anyFieldMap[anyFieldName].PropertySetter(domainObject, v));
                    }

                    var convertedOpenApiAny = OpenApiAnyConverter.GetSpecificOpenApiAny(value, schema);
                    anyFieldMap[anyFieldName].PropertySetter(domainObject, convertedOpenApiAny);
                }
                catch (OpenApiException exception)
                {
                    exception.Pointer = mapNode.Context.GetLocation();
                    mapNode.Diagnostic.Errors.Add(new OpenApiError(exception));
                }
                finally
                {
                    mapNode.Context.EndObject();
                }
            }
        }

        private static void ProcessAnyListFields<T>(
            MapNode mapNode,
            T domainObject,
            AnyListFieldMap<T> anyListFieldMap)
        {
            foreach (var anyListFieldName in anyListFieldMap.Keys.ToList())
            {
                try
                {
                    var newProperty = new List<IOpenApiAny>();

                    mapNode.Context.StartObject(anyListFieldName);

                    foreach (var propertyElement in anyListFieldMap[anyListFieldName].PropertyGetter(domainObject))
                    {
                        var schema = anyListFieldMap[anyListFieldName].SchemaGetter(domainObject);
                        if (schema?.Reference != null)
                        {
                            var index = newProperty.Count;
                            ScheduleAnyFieldConversion(
                                mapNode,
                                propertyElement,
                                schema,
                                v => newProperty[index] = v);
                        }

                        newProperty.Add(OpenApiAnyConverter.GetSpecificOpenApiAny(propertyElement, schema));
                    }

                    anyListFieldMap[anyListFieldName].PropertySetter(domainObject, newProperty);
                }
                catch (OpenApiException exception)
                {
                    exception.Pointer = mapNode.Context.GetLocation();
                    mapNode.Diagnostic.Errors.Add(new OpenApiError(exception));
                }
                finally
                {
                    mapNode.Context.EndObject();
                }
            }
        }

        private static void ProcessAnyMapFields<T, U>(
            MapNode mapNode,
            T domainObject,
            AnyMapFieldMap<T, U> anyMapFieldMap)
        {
            foreach (var anyMapFieldName in anyMapFieldMap.Keys.ToList())
            {
                try
                {
                    var newProperty = new List<IOpenApiAny>();
                    var schema = anyMapFieldMap[anyMapFieldName].SchemaGetter(domainObject);

                    mapNode.Context.StartObject(anyMapFieldName);

                    foreach (var propertyMapElement in anyMapFieldMap[anyMapFieldName].PropertyMapGetter(domainObject))
                    {
                        if (propertyMapElement.Value != null)
                        {
                            mapNode.Context.StartObject(propertyMapElement.Key);

                            var any = anyMapFieldMap[anyMapFieldName].PropertyGetter(propertyMapElement.Value);
                            if (schema?.Reference != null)
                            {
                                ScheduleAnyFieldConversion(
                                    mapNode,
                                    any,
                                    schema,
                                    v => anyMapFieldMap[anyMapFieldName].PropertySetter(propertyMapElement.Value, v));
                            }

                            var newAny = OpenApiAnyConverter.GetSpecificOpenApiAny(any, schema);
                            anyMapFieldMap[anyMapFieldName].PropertySetter(propertyMapElement.Value, newAny);
                        }
                    }
                }
                catch (OpenApiException exception)
                {
                    exception.Pointer = mapNode.Context.GetLocation();
                    mapNode.Diagnostic.Errors.Add(new OpenApiError(exception));
                }
                finally
                {
                    mapNode.Context.EndObject();
                }
            }
        }

        private static void SetupDelayedAnyFieldConversion(ParsingContext context)
        {
            context.SetTempStorage("anyFieldConversion", new List<Tuple<ParsingContext, OpenApiDiagnostic, string[], IOpenApiAny, OpenApiSchema, Action<IOpenApiAny>>>());
        }

        private static void ScheduleAnyFieldConversion(MapNode mapNode, IOpenApiAny value, OpenApiSchema schema, Action<IOpenApiAny> setter)
        {
            var schedule = mapNode.Context.GetFromTempStorage<List<Tuple<ParsingContext, OpenApiDiagnostic, string[], IOpenApiAny, OpenApiSchema, Action<IOpenApiAny>>>>("anyFieldConversion");
            if (schedule != null)
            {
                schedule.Add(Tuple.Create(mapNode.Context, mapNode.Diagnostic, mapNode.Context.CaptureLocation(), value, schema, setter));
            }
        }

        private static void ProcessAnyFieldConversion(ParsingContext context, OpenApiDocument document)
        {
            var schedule = context.GetFromTempStorage<List<Tuple<ParsingContext, OpenApiDiagnostic, string[], IOpenApiAny, OpenApiSchema, Action<IOpenApiAny>>>>("anyFieldConversion");
            if (schedule == null)
            {
                return;
            }
            context.SetTempStorage("anyFieldConversion", null);

            foreach (var item in schedule)
            {
                if (item.Item5.Reference == null)
                {
                    continue;
                }

                if (document.ResolveReference(item.Item5.Reference) is OpenApiSchema schema)
                {
                    var oldLocation = context.CaptureLocation();
                    try
                    {
                        context.SetLocation(item.Item3);
                        var convertedOpenApiAny = OpenApiAnyConverter.GetSpecificOpenApiAny(item.Item4, schema);
                        item.Item6(convertedOpenApiAny);
                    }
                    catch (OpenApiException exception)
                    {
                        exception.Pointer = context.GetLocation();
                        item.Item2.Errors.Add(new OpenApiError(exception));
                    }
                    finally
                    {
                        context.SetLocation(oldLocation);
                    }
                }
            }
        }

        private static RuntimeExpression LoadRuntimeExpression(ParseNode node)
        {
            var value = node.GetScalarValue();
            return RuntimeExpression.Build(value);
        }

        private static RuntimeExpressionAnyWrapper LoadRuntimeExpressionAnyWrapper(ParseNode node)
        {
            var value = node.GetScalarValue();

            if (value != null && value.StartsWith("$"))
            {
                return new RuntimeExpressionAnyWrapper
                {
                    Expression = RuntimeExpression.Build(value)
                };
            }

            return new RuntimeExpressionAnyWrapper
            {
                Any = OpenApiAnyConverter.GetSpecificOpenApiAny(node.CreateAny())
            };
        }

        public static IOpenApiAny LoadAny(ParseNode node)
        {
            return OpenApiAnyConverter.GetSpecificOpenApiAny(node.CreateAny());
        }

        private static IOpenApiExtension LoadExtension(string name, ParseNode node)
        {
            if (node.Context.ExtensionParsers.TryGetValue(name, out var parser))
            {
                return parser(
                    OpenApiAnyConverter.GetSpecificOpenApiAny(node.CreateAny()),
                    OpenApiSpecVersion.OpenApi3_0);
            }
            else
            {
                return OpenApiAnyConverter.GetSpecificOpenApiAny(node.CreateAny());
            }
        }

        private static string LoadString(ParseNode node)
        {
            return node.GetScalarValue();
        }
    }
}