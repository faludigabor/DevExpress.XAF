﻿using System;
using System.Reactive;
using System.Reactive.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using Fasterflect;
using Xpand.Extensions.ObjectExtensions;
using Xpand.Extensions.Reactive.Transform;
using Xpand.XAF.Modules.Reactive.Extensions;

namespace Xpand.XAF.Modules.Reactive.Services.Security{
    public static class SecurityExtensions{
        public static bool IsSecurityStrategyComplex(this ISecurityStrategyBase strategyBase) => strategyBase
            .IsInstanceOf("DevExpress.ExpressApp.Security.SecurityStrategyComplex");
        
        public static IObservable<Unit> Logon(this XafApplication application,object userKey) =>
            RxApp.AuthenticateSubject.Where(_ => _.authentication== application.Security.GetPropertyValue("Authentication"))
                .Do(_ => _.args.Instance=userKey).SelectMany(_ => application.WhenLoggedOn().FirstAsync()).ToUnit()
                .Merge(Unit.Default.ReturnObservable().Do(_ => application.Logon()).IgnoreElements())
                .TraceRX(unit => $"{userKey}");
    }
}