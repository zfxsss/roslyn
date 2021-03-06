﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Roslyn.Utilities
{
    /// <summary>
    /// Represents a single item or many items.
    /// </summary>
    /// <remarks>
    /// Used when a collection usually contains a single item but sometimes might contain multiple.
    /// </remarks>
    internal struct OneOrMany<T>
    {
        private readonly T _one;
        private readonly ImmutableArray<T> _many;

        public OneOrMany(T one)
        {
            _one = one;
            _many = default(ImmutableArray<T>);
        }

        public OneOrMany(ImmutableArray<T> many)
        {
            if (many.IsDefault)
            {
                throw new ArgumentNullException(nameof(many));
            }

            _one = default(T);
            _many = many;
        }

        public T this[int index]
        {
            get
            {
                if (_many.IsDefault)
                {
                    if (index != 0)
                    {
                        throw new IndexOutOfRangeException();
                    }

                    return _one;
                }
                else
                {
                    return _many[index];
                }
            }
        }

        public int Count
        {
            get
            {
                return _many.IsDefault ? 1 : _many.Length;
            }
        }

        public OneOrMany<T> Add(T one)
        {
            var builder = ArrayBuilder<T>.GetInstance();
            if (_many.IsDefault)
            {
                builder.Add(_one);
            }
            else
            {
                builder.AddRange(_many);
            }
            builder.Add(one);
            return new OneOrMany<T>(builder.ToImmutableAndFree());
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        internal struct Enumerator
        {
            private readonly OneOrMany<T> _collection;
            private int _index;

            internal Enumerator(OneOrMany<T> collection)
            {
                _collection = collection;
                _index = -1;
            }

            public bool MoveNext()
            {
                _index++;
                return _index < _collection.Count;
            }

            public T Current
            {
                get { return _collection[_index]; }
            }
        }
    }

    internal static class OneOrMany
    {
        public static OneOrMany<T> Create<T>(T one)
        {
            return new OneOrMany<T>(one);
        }

        public static OneOrMany<T> Create<T>(ImmutableArray<T> many)
        {
            return new OneOrMany<T>(many);
        }
    }
}
