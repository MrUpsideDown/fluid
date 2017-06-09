﻿using System;
using System.Collections.Generic;
using System.Linq;
using Fluid.Ast;
using Fluid.Ast.Values;
using Irony.Parsing;
using Microsoft.Extensions.Primitives;

namespace Fluid
{
    /// <summary>
    /// A Parser implementation based on Irony.
    /// </summary>
    public class FluidParser
    {
        private static LanguageData _language = new LanguageData(new FluidGrammar());

        // Used to store statements as we process tag bodies. 
        // Unstacked when we exit a tag.
        private Stack<(ParseTreeNode tag, List<Statement> statements)> _accumulators;
        private List<Statement> _accumulator;

        public bool TryParse(StringSegment template, out List<Statement> result, out IEnumerable<string> errors)
        {
            result = new List<Statement>();
            errors = Array.Empty<string>();
            
            Parser parser = null;
            _accumulators = new Stack<(ParseTreeNode tag, List<Statement> statements)>();

            try
            {
                int previous = 0, index = 0;
                Statement s;

                while (true)
                {
                    previous = index;
                    
                    if (!MatchTag(template, index, out var start, out var end))                    
                    {
                        index = template.Length;

                        if (index != previous)
                        {
                            // Consume last Text statement
                            var textSatement = CreateTextStatement(template, previous, index);
                            if (textSatement != null)
                            {
                                (_accumulator ?? result).Add(textSatement);
                            }
                        }

                        break;
                    }
                    else
                    {
                        if (parser == null) 
                        {
                            parser = new Parser(_language);
                        } 
                
                        if (start != previous)
                        {
                            // Consume current Text statement
                            var textStatement = CreateTextStatement(template, previous, start);
                            if (textStatement != null)
                            {
                                (_accumulator ?? result).Add(textStatement);
                            }
                        }

                        var tag = template.Substring(start, end - start + 1);
                        var tree = parser.Parse(tag);

                        if (tree.HasErrors())
                        {
                            errors = tree
                                .ParserMessages
                                .Select(x => $"{x.Message} at line:{x.Location.Line}, col:{x.Location.Column}")
                                .ToArray();

                            return false;
                        }

                        switch (tree.Root.Term.Name)
                        {
                            case "output":
                                s = BuildOutputStatement(tree.Root);
                                break;
                            case "tag":
                                s = BuildTagStatement(tree.Root);
                                break;
                            default:
                                s = null;
                                break;
                        }

                        if (s != null)
                        {
                            (_accumulator ?? result).Add(s);
                        }

                        index = end + 1;
                    }
                }

                return true;
            }
            catch (ParseException e)
            {
                errors = new [] { e.Message };
            }

            return false;
        }

        /// <summary>
        /// Returns a <see cref="TextStatement"/> where the extra whitespace is stripped 
        /// for a Tag that is the only content on a line
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        private TextStatement CreateTextStatement(StringSegment segment, int start, int end)
        {
            int index;

            if (end < segment.Length - 1 && segment.Value[end + 1] == '%')
            {
                index = end - 1;

                // There is a tag after, we can try to strip the end of the section
                while (true)
                {
                    // Reach beginning of section?
                    if (index == start)
                    {
                        end = start;
                        break;
                    }

                    var c = segment.Value[index];

                    if (c == '\n')
                    {
                        // Beginning of line, we can strip
                        end = index + 1;
                        break;
                    }

                    if (!Char.IsWhiteSpace(c))
                    {
                        // This is not just whitespace
                        break;
                    }

                    index--;
                }
            }

            if (start > 2 && segment.Value[start - 2] == '%')
            {
                index = start;

                // There is a tag before, we can try to strip the beginning of the section
                while (true)
                {
                    // Reach end of section?
                    if (index == end)
                    {
                        start = end;
                        break;
                    }

                    var c = segment.Value[index];

                    if (c == '\n' && index + 1 <= end)
                    {
                        // End of line, we can strip
                        start = index + 1;
                        break;
                    }

                    if (c == '\r' && index + 2 < end && segment.Value[index + 1] == '\n')
                    {
                        start = index + 2;
                        break;
                    }

                    if (!Char.IsWhiteSpace(c))
                    {
                        // This is not just whitespace
                        break;
                    }

                    index++;
                }
            }

            // Did the text get completely removed?
            if (end == start)
            {
                return null;
            }

            return new TextStatement(segment.Substring(start, end - start));
        }

        private bool MatchTag(StringSegment template, int startIndex, out int start, out int end)
        {
            start = -1;
            end = -1;

            while (startIndex < template.Length)
            {
                start = template.IndexOf('{', startIndex);
                
                // No match
                if (start == -1)
                {
                    end = -1;
                    return false;
                }

                if (start < template.Length - 1)
                {
                    var c = template.Value[start + 1];
                    if (c == '{' || c == '%')
                    {
                        // Start tag found
                        var endTag = c == '{' ? "}}" : "%}";

                        end = template.Value.IndexOf(endTag, start + 2);

                        if (end == -1)
                        {
                            // No end tag
                            return false;
                        }
                        else
                        {
                            // Found a match
                            end = end + 1;
                            return true;
                        }
                    }
                    else
                    {
                        // Was not a start tag, look further
                        startIndex = start + 1;
                    }
                }
                else
                {
                    return false;
                }                
            }

            return false;
        }

        public OutputStatement BuildOutputStatement(ParseTreeNode node)
        {
            var expressionNode = node.ChildNodes[0];
            var filterListNode = node.ChildNodes[1];

            var expression = BuildExpression(expressionNode);

            var filters = filterListNode.ChildNodes.Select(BuildFilterExpression).ToArray();

            return new OutputStatement(expression, filters);
        }

        public Statement BuildTagStatement(ParseTreeNode node)
        {
            var tag = node.ChildNodes[0];

            switch (tag.Term.Name)
            {
                case "for":
                    _accumulators.Push((tag, _accumulator = new List<Statement>()));
                    break;
                case "endFor":
                    (var t, var statements) = _accumulators.Pop();
                    _accumulator = _accumulators.Any() ? _accumulators.Peek().statements : null;
                    return BuildForStatement(t, statements);

                default:
                    throw new ParseException("Unknown expression type: " + node.Term.Name);
            }

            return null;
        }

        public Statement BuildForStatement(ParseTreeNode node, List<Statement> statements)
        {
            var identifier = node.ChildNodes[0].Token.Text;
            var source = node.ChildNodes[1];
            
            switch (source.Term.Name)
            {
                case "memberAccess":
                    return new ForStatement(statements, identifier, BuildMemberExpression(source));
                case "range":
                    return new ForStatement(statements, identifier, BuildRangeExpression(source));
                default:
                    throw new ParseException("Unknown for source type: " + node.Term.Name);
            }

        }

        public RangeExpression BuildRangeExpression(ParseTreeNode node)
        {
            Expression from = null, to = null;

            var fromNode = node.ChildNodes[0];

            from = fromNode.Term.Name == "number"
                ? (Expression)BuildLiteral(fromNode)
                : BuildMemberExpression(fromNode)
                ;

            var toNode = node.ChildNodes[1];

            to = toNode.Term.Name == "number"
                ? (Expression)BuildLiteral(toNode)
                : BuildMemberExpression(toNode)
                ;

            return new RangeExpression(from, to);
        }

        public Expression BuildExpression(ParseTreeNode node)
        {
            var child = node.ChildNodes[0];

            switch (child.Term.Name)
            {
                case "memberAccess":
                    return BuildMemberExpression(child);
                case "literal":
                    return BuildLiteralExpression(child);
                case "binaryExpression":
                    throw new NotSupportedException();
                default:
                    throw new ParseException("Unknown expression type: " + node.Term.Name);
            }
        }

        public MemberExpression BuildMemberExpression(ParseTreeNode node)
        {
            var identifierNode = node.ChildNodes[0];
            var segmentNodes = node.ChildNodes[1].ChildNodes;

            var segments = new MemberSegment[segmentNodes.Count + 1];
            segments[0] = new IdentifierSegmentIdentifer(identifierNode.Token.Text);

            for(var i=0; i<segmentNodes.Count; i++)
            {
                var segmentNode = segmentNodes[i];
                segments[i + 1] = BuildMemberSegment(segmentNode);
            }

            return new MemberExpression(segments);
        }

        public MemberSegment BuildMemberSegment(ParseTreeNode node)
        {
            var child = node.ChildNodes[0];

            switch (child.Term.Name)
            {
                case "memberAccessSegmentIdentifier":
                    return new IdentifierSegmentIdentifer(child.ChildNodes[0].Token.Text);
                case "memberAccessSegmentIndexer":
                    return new IndexerSegmentIdentifer(BuildExpression(child.ChildNodes[0]));
                default:
                    throw new ParseException("Unknown expression type: " + node.Term.Name);
            }
        }

        public LiteralExpression BuildLiteralExpression(ParseTreeNode node)
        {
            var child = node.ChildNodes[0];

            return BuildLiteral(child);
        } 

        public LiteralExpression BuildLiteral(ParseTreeNode node)
        {
            switch (node.Term.Name)
            {
                case "string":
                    return new LiteralExpression(new StringValue(node.Token.ValueString));

                case "number":
                    return new LiteralExpression(new NumberValue(Convert.ToDouble(node.Token.Value)));
                case "boolean":
                    if (!bool.TryParse(node.ChildNodes[0].Token.Text, out var boolean))
                    {
                        throw new ParseException("Invalid boolean: " + node.Token.Text);
                    }
                    return new LiteralExpression(new BooleanValue(boolean));

                default:
                    throw new ParseException("Unknown literal expression: " + node.Term.Name);
            }
        }

        public FilterExpression BuildFilterExpression(ParseTreeNode node)
        {
            var identifier = node.ChildNodes[0].Token.Text;

            var arguments = node.ChildNodes.Count > 1
                ? node.ChildNodes[1].ChildNodes.Select(BuildFilter).ToArray()
                : Array.Empty<Expression>()
                ;
            return new FilterExpression(identifier, arguments);
        }

        public Expression BuildFilter(ParseTreeNode node)
        {
            if (node.Term.Name == "memberAccess")
            {
                return BuildMemberExpression(node);
            }
            else
            {
                return BuildLiteral(node);
            }
        }
        
    }
}
