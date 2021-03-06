// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// THIS FILE IS NOT INTENDED TO BE EDITED UNLESS YOU ARE WORKING IN THE Recipes PROJECT. 
// 
// It has been imported using NuGet. 
// 
// This file can be updated in-place using the Package Manager Console. To check for updates, run the following command:
// 
// PM> Get-Package -Updates

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace System.Linq
{
#if !RecipesProject
    [DebuggerStepThrough]
    [ExcludeFromCodeCoverage]
#endif
    internal static partial class EnumerableExtensions
    {
        internal static IEnumerable<T> Do<T>(this IEnumerable<T> items, Action<T> action) =>
            items.Select(item =>
            {
                action(item);
                return item;
            });

        internal static void Run<TSource>(this IEnumerable<TSource> source)
        {
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                }
            }
        }

        internal static void ForEach<TSource>(this IEnumerable<TSource> source, Action<TSource> action) => source?.Do(action).Run();

        internal static IEnumerable<T> FlattenDepthFirst<T>(this T startNode, Func<T, IEnumerable<T>> getNodes)
        {
            yield return startNode;
            foreach (var node in getNodes(startNode))
            {
                foreach (var ancestor in node.FlattenDepthFirst(getNodes))
                {
                    yield return ancestor;
                }
            }
        }

        internal static IOrderedEnumerable<T> OrderByRandom<T>(this IEnumerable<T> source)
        {
            var random = new Random();
            return source.OrderBy(_ => random.Next());
        }

        internal static IOrderedEnumerable<T> ThenByRandom<T>(this IOrderedEnumerable<T> source)
        {
            var random = new Random();
            return source.ThenBy(_ => random.Next());
        }

        public static string ToDelimitedString(this IEnumerable<string> source, string separator) =>
            string.Join(separator, source.ToArray());
    }
}