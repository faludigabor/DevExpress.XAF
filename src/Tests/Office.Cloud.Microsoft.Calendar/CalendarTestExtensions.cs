﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using DevExpress.ExpressApp;
using DevExpress.Persistent.Base.General;
using DevExpress.Persistent.BaseImpl;
using Microsoft.Graph;
using Shouldly;
using Xpand.Extensions.Office.Cloud;
using Xpand.Extensions.Office.Cloud.BusinessObjects;
using Xpand.Extensions.Reactive.Transform;
using Xpand.Extensions.Reactive.Utility;
using Xpand.Extensions.XAF.ModelExtensions;
using Xpand.Extensions.XAF.XafApplicationExtensions;
using Xpand.TestsLib;
using Xpand.XAF.Modules.Office.Cloud.Microsoft.Tests;
using Xpand.XAF.Modules.Reactive;
using Event = Microsoft.Graph.Event;
using Platform = Xpand.Extensions.XAF.XafApplicationExtensions.Platform;
using Task = System.Threading.Tasks.Task;

namespace Xpand.XAF.Modules.Office.Cloud.Microsoft.Calendar.Tests{
	static class CalendarTestExtensions{
        public const string CalendarName = "Xpnad Events";
        public const string PagingCalendarName = "Xpand Paging Events";

        public const int PagingCalendarItemsCount = 11;
        
        public static async Task Populate_Modified<TEntity>(this IObjectSpaceProvider objectSpaceProvider,
            Func<CloudOfficeTokenStorage,IObservable<TEntity>> listEntities,IObservable<Unit> modified,TimeSpan timeout,Action<IObservable<TEntity>> assert){
            
            
            using (var objectSpace = objectSpaceProvider.CreateObjectSpace()){
                var tokenStorage = objectSpace.CreateObject<CloudOfficeTokenStorage>();
                var storageToken = tokenStorage.Token;
                await listEntities(tokenStorage);

                await modified.Timeout(timeout);

                var entities = listEntities(tokenStorage).SubscribeReplay().Do(entity => { });
                await entities.Timeout(timeout);

                tokenStorage.Token.ShouldNotBeNull();
                tokenStorage.Token.ShouldNotBe(storageToken);
                
                assert(entities);
            }
        }

        public static async Task<(ICalendarRequestBuilder requestBuilder, Frame frame)> InitializeService(this XafApplication application,string taskFolderName=CalendarName,bool keepTasks=false,bool keepTaskFolder=false){
            var modelTodo = application.Model.ToReactiveModule<IModelReactiveModuleOffice>().Office.Microsoft().Calendar();
            modelTodo.DefaultCaledarName = taskFolderName;
            var client = await application.InitGraphServiceClient();
            var foldersRequestBuilder = client.client.Me.Calendars;
            var taskFolder = await foldersRequestBuilder.GetCalendar(taskFolderName, !keepTaskFolder && taskFolderName!=PagingCalendarName);
            if (taskFolder == null&&taskFolderName==PagingCalendarName){
                taskFolder = await foldersRequestBuilder.Request()
                    .AddAsync(new global::Microsoft.Graph.Calendar(){Name = PagingCalendarName});
                await foldersRequestBuilder[taskFolder.Id].NewCalendarEvents(PagingCalendarItemsCount, nameof(MicrosoftCalendarModule));
            }
            var requestBuilder = foldersRequestBuilder[taskFolder?.Id];
            if (taskFolderName != PagingCalendarName&&!keepTasks&&!keepTaskFolder){
                await requestBuilder.DeleteAllEvents();
                (await requestBuilder.Events.ListAllItems()).Length.ShouldBe(0);
            }
            
            return (requestBuilder,client.frame);
        }

        public static async Task<(Frame frame, GraphServiceClient client)> InitGraphServiceClient(this XafApplication application){
            application.ObjectSpaceProvider.NewMicrosoftAuthentication();
            var todoModel = await application.ReactiveModulesModel().OfficeModel().MicrosoftModel().CalendarModel();
            var window = application.CreateViewWindow();
            window.SetView(application.NewView(todoModel.ObjectViews().First().ObjectView));
            var client = await CalendarService.Client.FirstAsync().ToTaskWithoutConfigureAwait();
            return client;
        }

        public static MicrosoftCalendarModule CalendarModule(this Platform platform,params ModuleBase[] modules){
            var application = NewApplication(platform,  modules);
            application.SetupSecurity();
            var module = application.AddModule<MicrosoftCalendarModule>(typeof(DevExpress.Persistent.BaseImpl.Event));
            application.Model.ConfigureMicrosoft();
            var todoModel = application.Model.ToReactiveModule<IModelReactiveModuleOffice>().Office.Microsoft().Calendar();
            var dependency = ((IModelObjectViews) todoModel).ObjectViews.AddNode<IModelObjectViewDependency>();
            dependency.ObjectView = application.Model.BOModel.GetClass(typeof(DevExpress.Persistent.BaseImpl.Event)).DefaultDetailView;
            application.Logon();
            application.CreateObjectSpace();
            return module.Application.Modules.OfType<MicrosoftCalendarModule>().First();  
        }

        static XafApplication NewApplication(this Platform platform,  ModuleBase[] modules){
            var xafApplication = platform.NewApplication<MicrosoftCalendarModule>();
            xafApplication.Modules.AddRange(modules);
            return xafApplication;
        }

        public static void AssertEvent(this IObjectSpaceProvider objectSpaceProvider, Type cloudEntityType, DevExpress.Persistent.BaseImpl.Event task,
            string title, DateTime? due, string taskId, string localTaskSubject){
            title.ShouldBe(localTaskSubject);
            
            due.ShouldNotBeNull();
            

            using (var space = objectSpaceProvider.CreateObjectSpace()){
                var cloudObjects = space.QueryCloudOfficeObject(cloudEntityType,task).ToArray();
                cloudObjects.Length.ShouldBe(1);
                var cloudObject = cloudObjects.First();
                cloudObject.LocalId.ShouldBe(task.Oid.ToString());
                cloudObject.CloudId.ShouldBe(taskId);
            }
        }
        public static async Task Delete_Event_Resource<TEvent>(this IObjectSpace objectSpace, Func<DevExpress.Persistent.BaseImpl.Event, IObservable<TEvent>> synchronizeEvents,
            Func<Task> assert,TimeSpan timeout){
            
            var localEvent = objectSpace.NewEvent();
            var calendarEvents = synchronizeEvents(localEvent)
                .FirstAsync().SubscribeReplay();
            objectSpace.CommitChanges();
            await calendarEvents.Timeout(timeout);

            calendarEvents = synchronizeEvents(localEvent)
                .FirstAsync().SubscribeReplay();
            localEvent.Resources.Remove(localEvent.Resources.First());
            objectSpace.CommitChanges();


            await calendarEvents.Timeout(timeout);

            await assert();
        }

        public static async Task<IList<(DevExpress.Persistent.BaseImpl.Event local, Event cloud)>> CreateExistingObjects(
            this XafApplication application, string title,int count=1){
            var builder =await application.AuthorizeTestMS();
            var calendar = await builder.Me.Calendars.GetCalendar(CalendarName, true);
            await builder.Me.Calendars[calendar.Id].DeleteAllEvents();
            return await Observable.Range(0, count).SelectMany(i => {
                return builder.Me.Calendars[calendar.Id].NewCalendarEvents(1, title)
                    .SelectMany(lst => lst).Select(outlookTask1 => (application.NewEvent(), outlookTask1));
            }).Buffer(count);
        }

        public static void Modify_Event<TTask>(this TTask task,  int i) where TTask:IEvent{
            task.Subject = $"{nameof(Modify_Event)}{i}";
        }
        
        public static DevExpress.Persistent.BaseImpl.Event NewEvent(this XafApplication application) {
            using (var objectSpace = application.CreateObjectSpace()){
                var newEvent = objectSpace.NewEvent();
                objectSpace.CommitChanges();
                return newEvent;
            }
        }
        public static DevExpress.Persistent.BaseImpl.Event NewEvent(this IObjectSpace objectSpace,int index=0) {
            var @event = objectSpace.CreateObject<DevExpress.Persistent.BaseImpl.Event>();
            @event.Subject = $"Subject{index}";
            
            @event.StartOn=DateTime.Now.AddDays(1);
            @event.EndOn = @event.StartOn.AddMinutes(30);
            var resource = objectSpace.CreateObject<Resource>();
            resource.Caption = "organizer@mail.com";
            @event.Resources.Add(resource);
            return @event;
        }

        public static IObservable<Unit> DeleteAllEvents(this ICalendarRequestBuilder builder){
            return builder.Events.ListAllItems().DeleteAll(task => builder.Me().Outlook.Tasks[task.Id].Request().DeleteAsync().ToObservable());
        }

        public static IObservable<Unit> DeleteAll(this IObservable<IEnumerable<Entity>> source, Func<Entity, IObservable<Unit>> delete){
            return source.Aggregate((acc, curr) => acc.Concat(curr)).SelectMany(tasks => tasks)
                .SelectMany(delete).LastOrDefaultAsync();
        }


        public static IObservable<IList<Event>> NewCalendarEvents(this ICalendarRequestBuilder builder,int count,string title){
            var dateTime = DateTime.Now;
            return Observable.Range(0, count).SelectMany(i => {
                var task = new Event(){
                    Subject = $"{i}{title}",
                    End = DateTimeTimeZone.FromDateTime(dateTime),Start = DateTimeTimeZone.FromDateTime(dateTime)
                };
                
                return builder.Events.Request().AddAsync(task);
            }).Buffer(count);
        }
        
    }
}