﻿using YesSql.Core.Indexes;

namespace YesSql.Samples.Hi.Indexes
{
    public class BlogPostByAuthor : MapIndex
    {
        public string Author { get; set; }
    }
}
