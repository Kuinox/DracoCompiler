using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Draco.Query.Tasks;

namespace Draco.Query;

// TODO: Thread-safety

/// <summary>
/// The type that manages the memoization and garbage collection of query results.
/// </summary>
public static class QueryDatabase
{
    // NOTE: We might be able to get rid of this is we lock results as a whole
    private sealed class EmptyConcurrentBag<T> : IProducerConsumerCollection<T>
    {
        public static EmptyConcurrentBag<T> Instsance { get; } = new();

        public int Count => 0;
        public bool IsSynchronized => true;
        public object SyncRoot => this;

        public void CopyTo(T[] array, int index) { }
        public void CopyTo(Array array, int index) { }
        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
        public T[] ToArray() => Array.Empty<T>();
        public bool TryAdd(T item) => throw new NotSupportedException();
        public bool TryTake([MaybeNullWhen(false)] out T item) => throw new NotSupportedException();
    }

    /// <summary>
    /// The interface of computed query results.
    /// </summary>
    private interface IResult
    {
        /// <summary>
        /// The revision where the result has last changed.
        /// </summary>
        public Revision ChangedAt { get; }

        /// <summary>
        /// The revision where the result has last been verified to be reusable.
        /// </summary>
        public Revision VerifiedAt { get; }

        /// <summary>
        /// The dependencies of this result.
        /// </summary>
        public IProducerConsumerCollection<IResult> Dependencies { get; }

        /// <summary>
        /// Refreshes this result.
        /// </summary>
        public Task Refresh();
    }

    /// <summary>
    /// Information about an input result.
    /// </summary>
    /// <typeparam name="T">The type of the input value.</typeparam>
    private sealed class InputResult<T> : IResult
    {
        public Revision ChangedAt { get; set; } = Revision.Invalid;
        public Revision VerifiedAt => Revision.MaxValue;
        public IProducerConsumerCollection<IResult> Dependencies => EmptyConcurrentBag<IResult>.Instsance;
        public T Value { get; set; } = default!;

        public Task Refresh() => Task.CompletedTask;
    }

    /// <summary>
    /// Information about a computed result.
    /// </summary>
    /// <typeparam name="T">The type of the computed value.</typeparam>
    private sealed class ComputedResult<T> : IResult
    {
        public Revision ChangedAt { get; set; } = Revision.Invalid;
        public Revision VerifiedAt { get; set; } = Revision.Invalid;
        public IProducerConsumerCollection<IResult> Dependencies { get; } = new ConcurrentBag<IResult>();
        public T Value { get; set; } = default!;

        private readonly QueryIdentifier identifier;

        public ComputedResult(QueryIdentifier identifier)
        {
            this.identifier = identifier;
        }

        public async Task Refresh() =>
            await QueryValueTaskMethodBuilder<T>.RunQueryByIdentifier(this.identifier);
    }

    /// <summary>
    /// The current revision the system is at.
    /// </summary>
    public static Revision CurrentRevision { get; private set; } = Revision.New;

    private static readonly ConcurrentDictionary<QueryIdentifier, IResult> queries = new();

    /// <summary>
    /// Creates an input for the system.
    /// </summary>
    /// <typeparam name="TResult">The type of the input value.</typeparam>
    /// <returns>The identifier for the input.</returns>
    public static QueryIdentifier CreateInput<TResult>()
    {
        var identifier = QueryIdentifier.New;
        queries.TryAdd(identifier, new InputResult<TResult>());
        return identifier;
    }

    /// <summary>
    /// Sets an input for the system.
    /// </summary>
    /// <typeparam name="TResult">The type of the input value.</typeparam>
    /// <param name="identifier">The identifier for the input.</param>
    /// <param name="value">The value to set the input to.</param>
    public static void SetInput<TResult>(QueryIdentifier identifier, TResult value)
    {
        // NOTE: Better error
        if (!queries.TryGetValue(identifier, out var cached)) throw new InvalidOperationException();
        var cachedResult = (InputResult<TResult>)cached;
        CurrentRevision = Revision.New;
        cachedResult.Value = value;
        cachedResult.ChangedAt = CurrentRevision;
    }

    /// <summary>
    /// Retrieves an input from the system.
    /// </summary>
    /// <typeparam name="TResult">The type of the input value.</typeparam>
    /// <param name="identifier">The identifier for the input.</param>
    /// <returns>The retrieved input as a task.</returns>
    public static QueryValueTask<TResult> GetInput<TResult>(QueryIdentifier identifier)
    {
        // NOTE: Better error
        if (!queries.TryGetValue(identifier, out var cached)) throw new InvalidOperationException();
        var cachedResult = (InputResult<TResult>)cached;
        return new(cachedResult.Value, identifier);
    }

    /// <summary>
    /// Called, when a query with new keys are called.
    /// </summary>
    /// <typeparam name="TResult">The result type of the query.</typeparam>
    /// <param name="identifier">The identifier of the query.</param>
    internal static void OnNewQuery<TResult>(QueryIdentifier identifier) =>
        // Add an empty entry
        queries.TryAdd(identifier, new ComputedResult<TResult>(identifier));

    /// <summary>
    /// Called, when a query has finished its computation.
    /// </summary>
    /// <typeparam name="TResult">The result type of the query.</typeparam>
    /// <param name="identifier">The identifier of the query.</param>
    /// <param name="result">The computed result of the query.</param>
    internal static void OnQueryResult<TResult>(QueryIdentifier identifier, TResult result)
    {
        // Refresh revision
        queries.AddOrUpdate(
            key: identifier,
            // NOTE: Should never happen
            addValueFactory: _ => throw new InvalidOperationException(),
            updateValueFactory: (_, cached) =>
            {
                var cachedResult = (ComputedResult<TResult>)cached;
                var changed = !object.Equals(result, cachedResult.Value);
                if (changed) cachedResult.ChangedAt = CurrentRevision;
                cachedResult.VerifiedAt = CurrentRevision;
                cachedResult.Value = result;
                return cachedResult;
            });
    }

    /// <summary>
    /// Called, when a dependency is discovered between two queries.
    /// </summary>
    /// <param name="dependent">The identifier of the query that is dependent on <paramref name="dependency"/>.</param>
    /// <param name="dependency">The query that is called by <paramref name="dependent"/>.</param>
    internal static void OnQueryDependency(QueryIdentifier dependent, QueryIdentifier dependency)
    {
        // NOTE: Should never happen
        if (!queries.TryGetValue(dependent, out var dependentResult)) throw new InvalidOperationException();
        // NOTE: Should never happen
        if (!queries.TryGetValue(dependency, out var dependencyResult)) throw new InvalidOperationException();
        dependentResult.Dependencies.TryAdd(dependencyResult);
    }

    /// <summary>
    /// Attempts to retrieve the up to date result of a query.
    /// </summary>
    /// <typeparam name="TResult">The result type of the query.</typeparam>
    /// <param name="identifier">The query identifier.</param>
    /// <param name="result">The retrieved result, if it's up to date.</param>
    /// <returns>True, if the query named <paramref name="identifier"/> has an up to date result and the result
    /// is written to <paramref name="result"/>.</returns>
    internal static bool TryGetUpToDateQueryResult<TResult>(
        QueryIdentifier identifier,
        [MaybeNullWhen(false)] out TResult result)
    {
        // NOTE: Should never happen
        if (!queries.TryGetValue(identifier, out var cached)) throw new InvalidOperationException();
        var cachedResult = (ComputedResult<TResult>)cached;
        // The value has never been memoized yet
        if (cachedResult.ChangedAt == Revision.Invalid)
        {
            // Force recomputation
            result = default;
            return false;
        }
        // Value is already memoized, but potentially outdated
        // If we have been verified to be valid already in the current version, we can just clone and return
        if (cachedResult.VerifiedAt == CurrentRevision)
        {
            result = cachedResult.Value!;
            return true;
        }
        // Check if the dependencies are up to date
        // TODO: This blocks synchronously but I have no idea if we can even make this method async
        Task.WaitAll(cachedResult.Dependencies.Select(dep => dep.Refresh()).ToArray());

        // Now check wether dependencies have been updated since this one
        if (cachedResult.Dependencies.All(dep => dep.ChangedAt <= cachedResult.VerifiedAt))
        {
            // All dependencies came from earlier revisions, they are safe to reuse
            // Which means this value is also safe to reuse, update verification number
            cachedResult.VerifiedAt = CurrentRevision;
            result = cachedResult.Value!;
            return true;
        }

        // Some values must have gone outdated and got recomputed, we also need to recompute, force recomputation
        result = default;
        return false;
    }
}
