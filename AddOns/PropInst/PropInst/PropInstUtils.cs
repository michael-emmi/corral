﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using cba.Util;
using Microsoft.Boogie;

namespace PropInst
{

    class FindIdentifiersVisitor : FixedVisitor
    {
        public readonly List<IdentifierExpr> Identifiers = new List<IdentifierExpr>();
        public override Expr VisitIdentifierExpr(IdentifierExpr node)
        {
            Identifiers.Add(node);
            return base.VisitIdentifierExpr(node);
        }
    }


    /// <summary>
    /// A visitor that executes a given Substitution.
    /// old: (Note that this visitor "consumes" the substitution template, i.e.,
    /// the replacements are not done in a copy but the template itself.)
    /// new: the substitution makes a deep copy of the template
    /// </summary>
    class SubstitionVisitor : FixedVisitor
    {
        private readonly Dictionary<string, IAppliable> _funcSub;
        private readonly Dictionary<Declaration, Expr> _substitution;


        public SubstitionVisitor(Dictionary<Declaration, Expr> psub)
            : this(psub, new Dictionary<string, IAppliable>(0))
        {
        }

        public SubstitionVisitor(Dictionary<Declaration, Expr> psub, Dictionary<string, IAppliable> pFuncSub )
        {
            _substitution = psub;
            _funcSub = pFuncSub;
        }

        public override Expr VisitIdentifierExpr(IdentifierExpr node)
        {
            if (node.Decl != null && _substitution.ContainsKey(node.Decl)) 
            {
                var replacement = _substitution[node.Decl];
                return replacement;
            }
            //if (_idSub.ContainsKey(node)) 
            //{
            //    var replacement = _idSub[node];
            //    return replacement;
            //}
            return new IdentifierExpr(node.tok, node.Decl, node.Immutable);
            //return base.VisitIdentifierExpr(node);
        }

        /////////////////
        // here begin the visit overrides that are only necessary for the cloning --> one might move them to a clone visitor..
        /////////////////

        public override AssignLhs VisitSimpleAssignLhs(SimpleAssignLhs node)
        {
            Expr e = VisitIdentifierExpr(node.AssignedVariable);
            if (!(e is IdentifierExpr))
            {
                throw new InvalidExpressionException("lhs must be an identifier, also after substitution --> malformed property??");
            }
            return new SimpleAssignLhs(node.tok, (IdentifierExpr) e);
        }

        public override AssignLhs VisitMapAssignLhs(MapAssignLhs node)
        {
            var dispatchedIndices = new List<Expr>();

            foreach (var ind in node.Indexes)
            {
                dispatchedIndices.Add(VisitExpr(ind));
            }

            AssignLhs newAssignLhs = null;
            if (node.Map is MapAssignLhs)
            {
                newAssignLhs = VisitMapAssignLhs(node.Map as MapAssignLhs);
            }
            else if (node.Map is SimpleAssignLhs)
            {
                newAssignLhs = VisitSimpleAssignLhs(node.Map as SimpleAssignLhs);
            }


            return new MapAssignLhs(node.tok, newAssignLhs, dispatchedIndices);
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            //we have to dispatch explicitly, here..
            var dispatchedArgs = new List<Expr>();
            foreach (var arg in node.Args)
            {
                dispatchedArgs.Add(VisitExpr(arg));
            }

            if (_funcSub.ContainsKey(node.Fun.FunctionName))
            {
                Debug.Assert(dispatchedArgs.Count == node.Args.Count); //otherwise use ANYFUNC or so..
                return new NAryExpr(Token.NoToken, _funcSub[node.Fun.FunctionName], dispatchedArgs);
            }

            //default: just put together the NAryExpression with the function from before
            return new NAryExpr(node.tok, node.Fun, dispatchedArgs);
        }

        public override Cmd VisitAssertCmd(AssertCmd node)
        {
            return new AssertCmd(node.tok, VisitExpr(node.Expr));
        }

        public override Cmd VisitAssignCmd(AssignCmd node)
        {
            var lhssDispatched = new List<AssignLhs>();
            foreach (var lhs in node.Lhss)
            {
                if (lhs is MapAssignLhs)
                {
                    lhssDispatched.Add(VisitMapAssignLhs(lhs as MapAssignLhs));
                }
                else if (lhs is SimpleAssignLhs)
                {
                    lhssDispatched.Add(VisitSimpleAssignLhs(lhs as SimpleAssignLhs));
                }
            }

            var rhssDispatched = new List<Expr>();

            foreach (var rhs in node.Rhss)
            {
                rhssDispatched.Add(VisitExpr(rhs));
            }


            return new AssignCmd(node.tok, lhssDispatched, rhssDispatched);
        }

        public override List<Cmd> VisitCmdSeq(List<Cmd> cmdSeq)
        {
            List<Cmd> dispatchedCmds = new List<Cmd>();

            foreach (var c in cmdSeq)
            {
                if (c is CallCmd)
                {
                    dispatchedCmds.Add(VisitCallCmd(c as CallCmd));
                }
                else if (c is AssignCmd)
                {
                    dispatchedCmds.Add(VisitAssignCmd(c as AssignCmd));
                }
                else if (c is AssertCmd)
                {
                    dispatchedCmds.Add(VisitAssertCmd(c as AssertCmd));
                }
                else if (c is AssumeCmd)
                {
                    dispatchedCmds.Add(VisitAssumeCmd(c as AssumeCmd));
                }
            }

            return dispatchedCmds;
        }
    }

    class ExprMatchVisitor : FixedVisitor
    {
        private List<Expr> _toConsume;
        public bool MayStillMatch = true;
        public readonly Dictionary<string, IAppliable> FunctionSubstitution = new Dictionary<string, IAppliable>();
        public readonly Dictionary<Declaration, Expr> Substitution = new Dictionary<Declaration, Expr>();


        public ExprMatchVisitor(List<Expr> pToConsume)
        {
            _toConsume = pToConsume;
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            //start with some negative cases
            if (!MayStillMatch
                ||_toConsume.Count == 0)
            {
                MayStillMatch = false;
                return base.VisitNAryExpr(node);
            }

            if (!(_toConsume.First() is NAryExpr))
            {
                //TODO: may still be an IdentifierExp intended to match any exp
                if (_toConsume.First() is IdentifierExpr)
                {
                    Substitution.Add(((IdentifierExpr) _toConsume.First()).Decl, node);
                    return node;
                }
            }

            var naeToConsume = (NAryExpr) _toConsume.First();


            if (((NAryExpr) _toConsume.First()).Args.Count != node.Args.Count)
            {
                MayStillMatch = false;
                return base.VisitNAryExpr(node);
            }

            // now the positive cases
            if (naeToConsume.Fun.Equals(node.Fun))
            {
                _toConsume = new List<Expr>(naeToConsume.Args);

                return base.VisitNAryExpr(node);
            } 
            if (naeToConsume.Fun is FunctionCall
                && ((FunctionCall) naeToConsume.Fun).Func != null)
            {
                var func = ((FunctionCall) naeToConsume.Fun).Func; //TODO: use attributes..

                _toConsume = new List<Expr>(naeToConsume.Args);
                if (!FunctionSubstitution.ContainsKey(naeToConsume.Fun.FunctionName))
                    FunctionSubstitution.Add(naeToConsume.Fun.FunctionName, node.Fun);//TODO: understand..
                return base.VisitNAryExpr(node);
            }
            MayStillMatch = false;
            return base.VisitNAryExpr(node);
        }

        public override Expr VisitIdentifierExpr(IdentifierExpr node)
        {
            if (!MayStillMatch
                || _toConsume.Count == 0)
            {
                MayStillMatch = false;
                return base.VisitIdentifierExpr(node);
            }
            if (!(_toConsume.First() is IdentifierExpr))
            {
                MayStillMatch = false;
                return base.VisitIdentifierExpr(node);
            }

            var idexToConsume = (IdentifierExpr) _toConsume.First();

            if (idexToConsume.Decl != null)
            {
                Substitution.Add(idexToConsume.Decl, node);
                _toConsume.RemoveAt(0);
                return base.VisitIdentifierExpr(node);
            }

            MayStillMatch = false;
            return base.VisitIdentifierExpr(node);
        }
        public override Expr VisitLiteralExpr(LiteralExpr node)
        {
            if (!MayStillMatch
                || _toConsume.Count == 0)
            {
                MayStillMatch = false;
                return base.VisitLiteralExpr(node);
            }

            if (!(_toConsume.First() is LiteralExpr))
            {
                //TODO: may still be an IdentifierExp intended to match any exp
                if (_toConsume.First() is IdentifierExpr)
                {
                    Substitution.Add(((IdentifierExpr) _toConsume.First()).Decl, node);
                    return node;
                }
            }
            if (node.Val.Equals(((LiteralExpr) _toConsume.First()).Val))
            {
                return base.VisitLiteralExpr(node);
            }
            MayStillMatch = false;
            return base.VisitLiteralExpr(node);
        }
    }

     class ProcedureSigMatcher
    {
        private readonly Procedure _toMatch;
        private readonly Implementation _impl;

        // idea: if we have ##ANYPARAMS specified in toMatch, then we may chose to filter parameters through these Attributs 
        // (as usual, only params are used whose attributes are a superset of the ones specified in toMatch)
        public QKeyValue ToMatchAnyParamsAttributes;

        public ProcedureSigMatcher(Procedure toMatch, Implementation impl)
        {
            _impl = impl;
            _toMatch = toMatch;
        }

         public bool MatchSig()
         {

             if (!Driver.AreAttributesASubset(_toMatch.Attributes, _impl.Attributes)) return false;

             if (_toMatch.Name.StartsWith("##"))
             {
                 //do nothing
             }
             else if (_toMatch.Name != _impl.Name)
             {
                 return false;
             }
             if (_toMatch.InParams.Count == 1 && _toMatch.InParams[0].Name == "##ANYPARAMS")
             {
                 ToMatchAnyParamsAttributes = _toMatch.InParams[0].Attributes;
             }
             else if (_toMatch.InParams.Count != _impl.InParams.Count)
             {
                 return false;
             }
             else
             {
                 for (int i = 0; i < _toMatch.InParams.Count; i++)
                 {
                     if (_toMatch.InParams[i].GetType() != _impl.InParams[i].GetType())
                         return false;
                 }
             }
             if (_toMatch.OutParams.Count == 1 && _toMatch.OutParams[0].Name == "##ANYPARAMS")
             {
                 //do nothing
             }
             else if (_toMatch.OutParams.Count != _impl.OutParams.Count)
             {
                 return false;
             }
             return true;
        }
    }

     public class GatherMemAccesses : FixedVisitor
     {
         public List<Tuple<Variable, Expr>> accesses;
         public GatherMemAccesses()
         {
             accesses = new List<Tuple<Variable, Expr>>();
         }

         public override Expr VisitForallExpr(ForallExpr node)
         {
             return node;
         }

         public override Expr VisitExistsExpr(ExistsExpr node)
         {
             return node;
         }

         public override Expr VisitNAryExpr(NAryExpr node)
         {
             if (node.Fun is MapSelect && node.Args.Count == 2 && node.Args[0] is IdentifierExpr)
             {
                 accesses.Add(Tuple.Create((node.Args[0] as IdentifierExpr).Decl, node.Args[1]));
             }

             return base.VisitNAryExpr(node);
         }
     }
}
