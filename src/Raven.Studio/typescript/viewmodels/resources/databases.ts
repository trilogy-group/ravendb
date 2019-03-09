import app = require("durandal/app");
import appUrl = require("common/appUrl");
import viewModelBase = require("viewmodels/viewModelBase");
import accessManager = require("common/shell/accessManager");
import deleteDatabaseConfirm = require("viewmodels/resources/deleteDatabaseConfirm");
import createDatabase = require("viewmodels/resources/createDatabase");
import disableDatabaseToggleConfirm = require("viewmodels/resources/disableDatabaseToggleConfirm");
import toggleDatabaseCommand = require("commands/resources/toggleDatabaseCommand");
import togglePauseIndexingCommand = require("commands/database/index/togglePauseIndexingCommand");
import toggleDisableIndexingCommand = require("commands/database/index/toggleDisableIndexingCommand");
import deleteDatabaseCommand = require("commands/resources/deleteDatabaseCommand");
import loadDatabaseCommand = require("commands/resources/loadDatabaseCommand");
import changesContext = require("common/changesContext");
import compactDatabaseCommand = require("commands/resources/compactDatabaseCommand");
import notificationCenter = require("common/notifications/notificationCenter");
import getIndexNamesCommand = require("commands/database/index/getIndexNamesCommand");
import databasesInfo = require("models/resources/info/databasesInfo");
import getDatabasesCommand = require("commands/resources/getDatabasesCommand");
import getDatabaseCommand = require("commands/resources/getDatabaseCommand");
import databaseInfo = require("models/resources/info/databaseInfo");
import messagePublisher = require("common/messagePublisher");
import clusterTopologyManager = require("common/shell/clusterTopologyManager");
import databaseGroupNode = require("models/resources/info/databaseGroupNode");
import databaseNotificationCenterClient = require("common/databaseNotificationCenterClient");
import changeSubscription = require("common/changeSubscription");
import databasesManager = require("common/shell/databasesManager");
import generalUtils = require("common/generalUtils");
import popoverUtils = require("common/popoverUtils");
import database = require("models/resources/database");
import eventsCollector = require("common/eventsCollector");

class databases extends viewModelBase {

    databases = ko.observable<databasesInfo>();
    clusterManager = clusterTopologyManager.default;
    
    formatBytes = generalUtils.formatBytesToSize;

    filters = {
        searchText: ko.observable<string>(),
        localOnly: ko.observable<string>()
    };

    selectionState: KnockoutComputed<checkbox>;
    selectedDatabases = ko.observableArray<string>([]);

    spinners = {
        globalToggleDisable: ko.observable<boolean>(false)
    };

    private static compactView = ko.observable<boolean>(false);
    compactView = databases.compactView;
    
    statsSubscription: changeSubscription;

    accessManager = accessManager.default.databasesView;
    isAboveUserAccess = accessManager.default.operatorAndAbove;

    environmentClass = (source: KnockoutObservable<Raven.Client.Documents.Operations.Configuration.StudioConfiguration.StudioEnvironment>) => 
        database.createEnvironmentColorComputed("label", source);
   
    constructor() {
        super();

        this.bindToCurrentInstance("newDatabase", "toggleDatabase", "togglePauseDatabaseIndexing", 
            "toggleDisableDatabaseIndexing", "deleteDatabase", "activateDatabase", "updateDatabaseInfo",
            "compactDatabase", "databasePanelClicked");

        this.initObservables();
    }

    private initObservables() {
        const filters = this.filters;

        filters.searchText.throttle(200).subscribe(() => this.filterDatabases());
        filters.localOnly.subscribe(() => this.filterDatabases());

        this.selectionState = ko.pureComputed<checkbox>(() => {
            const databases = this.databases().sortedDatabases().filter(x => !x.filteredOut());
            const selectedCount = this.selectedDatabases().length;
            if (databases.length && selectedCount === databases.length)
                return "checked";
            if (selectedCount > 0)
                return "some_checked";
            return "unchecked";
        });
    }

    // Override canActivate: we can always load this page, regardless of any system db prompt.
    canActivate(args: any): any {
        return true;
    }

    activate(args: any): JQueryPromise<Raven.Client.ServerWide.Operations.DatabasesInfo> {
        super.activate(args);

        // we can't use createNotifications here, as it is called after *database changes API* is connected, but user
        // can enter this view and never select database

        this.addNotification(this.changesContext.serverNotifications().watchAllDatabaseChanges((e: Raven.Server.NotificationCenter.Notifications.Server.DatabaseChanged) => this.onDatabaseChange(e)));
        this.addNotification(this.changesContext.serverNotifications().watchReconnect(() => this.fetchDatabases()));
        
        this.registerDisposable(this.changesContext.databaseNotifications.subscribe((dbNotifications) => this.onDatabaseChanged(dbNotifications)));
        
        return this.fetchDatabases();
    }
    
    private onDatabaseChanged(dbChanges: databaseNotificationCenterClient) {
        if (dbChanges) {

            const database = dbChanges.getDatabase();

            const throttledUpdate = _.throttle(() => {
                this.updateDatabaseInfo(database.name);
            }, 10000);
            
            this.statsSubscription = dbChanges.watchAllDatabaseStatsChanged(stats => {
                const matchedDatabase = this.databases().sortedDatabases().find(x => x.name === database.name);
                if (matchedDatabase) {
                    matchedDatabase.documentsCount(stats.CountOfDocuments);
                    matchedDatabase.indexesCount(stats.CountOfIndexes);
                }
                
                // schedule update of other properties
                throttledUpdate();
            });
        } else {
            if (this.statsSubscription) {
                this.statsSubscription.off();
                this.statsSubscription = null;
            }
        }
        
    }

    attached() {
        super.attached();
        this.updateHelpLink("Z8DC3Q");
        this.updateUrl(appUrl.forDatabases());
    }
    
    compositionComplete() {
        super.compositionComplete();
        
        this.initTooltips();
    }
    
    deactivate() {
        if (this.statsSubscription) {
            this.statsSubscription.off();
            this.statsSubscription = null;
        }
        
        // make we all propovers are hidden
        $('[data-toggle="more-nodes-tooltip"]').popover('hide');
    }

    private fetchDatabases(): JQueryPromise<Raven.Client.ServerWide.Operations.DatabasesInfo> {
        return new getDatabasesCommand()
            .execute()
            .done(info => this.databases(new databasesInfo(info)));
    }

    private onDatabaseChange(e: Raven.Server.NotificationCenter.Notifications.Server.DatabaseChanged) {
        switch (e.ChangeType) {
            case "Load":
            case "Put":
                this.updateDatabaseInfo(e.DatabaseName);
                break;

            case "RemoveNode":
            case "Delete":
                // since we don't know if database was removed from current node, let's fetch databaseInfo first
                this.updateDatabaseInfo(e.DatabaseName)
                    .fail((xhr: JQueryXHR) => {
                        if (xhr.status === 404) {
                            // database was removed from all nodes

                            const db = this.databases().sortedDatabases().find(rs => rs.name === e.DatabaseName);
                            if (db) {
                                this.removeDatabase(db);
                            }
                        }
                    });
                break;
        }
    }

    updateDatabaseInfo(databaseName: string) {
        return new getDatabaseCommand(databaseName)
            .execute()
            .done((result: Raven.Client.ServerWide.Operations.DatabaseInfo) => {
                this.databases().updateDatabase(result);
                this.filterDatabases();
                this.initTooltips();
            });
    }
    
    private initTooltips() {
        const self = this;

        const contentProvider = (dbInfo: databaseInfo) => {
            const nodesPart = dbInfo.nodes().map(node => {
                return `
                <a href="${this.createAllDocumentsUrlObservableForNode(dbInfo, node)()}" 
                    target="${node.tag() === this.clusterManager.localNodeTag() ? "" : "_blank"}" 
                    class="margin-left margin-right ${dbInfo.isBeingDeleted() ? "link-disabled" : ''}" 
                    title="${node.type()}">
                        <i class="${node.cssIcon()}"></i> <span>Node ${node.tag()}</span>
                    </a>
                `
            }).join(" ");
            
            return `<div class="more-nodes-tooltip">
                <div>
                    <i class="icon-dbgroup"></i>
                    <span>
                        Database Group for ${dbInfo.name}
                    </span>
                </div>
                <hr />
                <div class="flex-horizontal flex-wrap">
                    ${nodesPart}    
                </div>
            </div>`;
        };
        
        $('.databases [data-toggle="more-nodes-tooltip"]').each((idx, element) => {
            popoverUtils.longWithHover($(element), {
                content: () => { 
                    const context = ko.dataFor(element);
                    return contentProvider(context);
                },
                placement: "top",
                container: "body"
            });
        })
    }

    private filterDatabases(): void {
        const filters = this.filters;
        let searchText = filters.searchText();
        const hasSearchText = !!searchText;
        const localOnly = filters.localOnly();
        const nodeTag = this.clusterManager.localNodeTag();

        if (hasSearchText) {
            searchText = searchText.toLowerCase();
        }

        const matchesFilters = (db: databaseInfo) => {
            const matchesText = !hasSearchText || db.name.toLowerCase().indexOf(searchText) >= 0;
            const matchesLocal = !localOnly || db.isLocal(nodeTag);

            return matchesText && matchesLocal;
        };

        const databases = this.databases();
        databases.sortedDatabases().forEach(db => {
            const matches = matchesFilters(db);
            db.filteredOut(!matches);

            if (!matches) {
                this.selectedDatabases.remove(db.name);
            }
        });
    }

    createManageDbGroupUrlObsevable(dbInfo: databaseInfo): KnockoutComputed<string> {
        const isLocalObservable = this.createIsLocalDatabaseObservable(dbInfo.name);

        return ko.pureComputed(() => {
            const isLocal = isLocalObservable();
            const link = appUrl.forManageDatabaseGroup(dbInfo);
            if (isLocal) {
                return link;
            } else {
                return databases.toExternalUrl(dbInfo, link);
            }
        });
    }

    createAllDocumentsUrlObservable(dbInfo: databaseInfo): KnockoutComputed<string> {
        const isLocalObservable = this.createIsLocalDatabaseObservable(dbInfo.name);

        return ko.pureComputed(() => {
            const isLocal = isLocalObservable();
            const link = appUrl.forDocuments(null, dbInfo);
            if (isLocal) {
                return link;
            } else {
                return databases.toExternalUrl(dbInfo, link);
            }
        });
    }

    createAllDocumentsUrlObservableForNode(dbInfo: databaseInfo, node: databaseGroupNode) {
        return ko.pureComputed(() => {
            const currentNodeTag = this.clusterManager.localNodeTag();
            const nodeTag = node.tag();
            const link = appUrl.forDocuments(null, dbInfo);
            if (currentNodeTag === nodeTag) {
                return link;
            } else {
                return appUrl.toExternalUrl(node.serverUrl(), link);
            }
        });
    }

    private static toExternalUrl(dbInfo: databaseInfo, url: string) {
        // we have to redirect to different node, let's find first member where selected database exists
        const firstMember = dbInfo.nodes().find(x => x.type() === "Member");
        const serverUrl = firstMember ? firstMember.serverUrl() : clusterTopologyManager.default.localNodeUrl();
        return appUrl.toExternalUrl(serverUrl, url);
    }

    indexErrorsUrl(dbInfo: databaseInfo): string {
        return appUrl.forIndexErrors(dbInfo);
    }

    storageReportUrl(dbInfo: databaseInfo): string {
        return appUrl.forStatusStorageReport(dbInfo);
    }

    indexesUrl(dbInfo: databaseInfo): string {
        return appUrl.forIndexes(dbInfo);
    } 

    periodicBackupUrl(dbInfo: databaseInfo): string {
        return appUrl.forEditPeriodicBackupTask(dbInfo);
    }

    manageDatabaseGroupUrl(dbInfo: databaseInfo): string {
        return appUrl.forManageDatabaseGroup(dbInfo);
    }

    private getSelectedDatabases() {
        const selected = this.selectedDatabases();
        return this.databases().sortedDatabases().filter(x => _.includes(selected, x.name));
    }

    toggleSelectAll(): void {
        const selectedCount = this.selectedDatabases().length;

        if (selectedCount > 0) {
            this.selectedDatabases([]);
        } else {
            const namesToSelect = [] as Array<string>;

            this.databases().sortedDatabases().forEach(db => {
                if (!db.filteredOut()) {
                    namesToSelect.push(db.name);
                }
            });

            this.selectedDatabases(namesToSelect);
        }
    }

    deleteDatabase(db: databaseInfo) {
        this.deleteDatabases([db]);
    }

    deleteSelectedDatabases() {
       this.deleteDatabases(this.getSelectedDatabases());
    }

    private deleteDatabases(toDelete: databaseInfo[]) {
        const confirmDeleteViewModel = new deleteDatabaseConfirm(toDelete);

        confirmDeleteViewModel
            .result
            .done((confirmResult: deleteDatabaseConfirmResult) => {
                if (confirmResult.can) {   

                    const dbsList = toDelete.map(x => {
                        x.isBeingDeleted(true);
                        const asDatabase = x.asDatabase();

                        // disconnect here to avoid race condition between database deleted message
                        // and websocket disconnection
                        changesContext.default.disconnectIfCurrent(asDatabase, "DatabaseDeleted");
                        return asDatabase;
                    });
                                    
                    new deleteDatabaseCommand(dbsList, !confirmResult.keepFiles)
                        .execute();
                }
            });

        app.showBootstrapDialog(confirmDeleteViewModel);
    }

    private removeDatabase(dbInfo: databaseInfo) {
        this.databases().sortedDatabases.remove(dbInfo);
        this.selectedDatabases.remove(dbInfo.name);
        messagePublisher.reportSuccess(`Database ${dbInfo.name} was successfully deleted`);
    }

    enableSelectedDatabases() {
        this.toggleSelectedDatabases(true);
    }

    disableSelectedDatabases() {
        this.toggleSelectedDatabases(false);
    }

    private toggleSelectedDatabases(enableAll: boolean) {
        const selectedDatabases = this.getSelectedDatabases().map(x => x.asDatabase());

        if (_.every(selectedDatabases, x => x.disabled() !== enableAll)) {
            return;
        }

        if (selectedDatabases.length > 0) {
            const disableDatabaseToggleViewModel = new disableDatabaseToggleConfirm(selectedDatabases, !enableAll);

            disableDatabaseToggleViewModel.result.done(result => {
                if (result.can) {
                    this.spinners.globalToggleDisable(true);

                    new toggleDatabaseCommand(selectedDatabases, !enableAll)
                        .execute()
                        .done(disableResult => {
                            disableResult.Status.forEach(x => this.onDatabaseDisabled(x));
                        })
                        .always(() => this.spinners.globalToggleDisable(false));
                }
            });

            app.showBootstrapDialog(disableDatabaseToggleViewModel);
        }
    }

    toggleDatabase(rsInfo: databaseInfo) {
        const disable = !rsInfo.disabled();

        const rs = rsInfo.asDatabase();
        const disableDatabaseToggleViewModel = new disableDatabaseToggleConfirm([rs], disable);

        disableDatabaseToggleViewModel.result.done(result => {
            if (result.can) {
                rsInfo.inProgressAction(disable ? "Disabling..." : "Enabling...");

                new toggleDatabaseCommand([rs], disable)
                    .execute()
                    .done(disableResult => {
                        disableResult.Status.forEach(x => this.onDatabaseDisabled(x));
                    })
                    .always(() => rsInfo.inProgressAction(null));
            }
        });

        app.showBootstrapDialog(disableDatabaseToggleViewModel);
    }

    private onDatabaseDisabled(result: disableDatabaseResult) {
        const dbs = this.databases().sortedDatabases();
        const matchedDatabase = dbs.find(rs => rs.name === result.Name);

        if (matchedDatabase) {
            matchedDatabase.disabled(result.Disabled);

            // If Enabling a database (that is selected from the top) than we want it to be Online(Loaded)
            if (matchedDatabase.isCurrentlyActiveDatabase() && !matchedDatabase.disabled()) {
                new loadDatabaseCommand(matchedDatabase.asDatabase())
                    .execute();
            }
        }
    }

    toggleDisableDatabaseIndexing(db: databaseInfo) {
        const enableIndexing = db.indexingDisabled();
        const message = enableIndexing ? "Enable" : "Disable";

        eventsCollector.default.reportEvent("databases", "toggle-indexing");

        this.confirmationMessage("Are you sure?", message + " indexing?")
            .done(result => {
                if (result.can) {
                    db.inProgressAction(enableIndexing ? "Enabling..." : "Disabling...");

                    new toggleDisableIndexingCommand(enableIndexing, db)
                        .execute()
                        .done(() => {
                            db.indexingDisabled(!enableIndexing);
                            db.indexingPaused(false);
                        })
                        .always(() => db.inProgressAction(null));
                }
            });
    }
    
    compactDatabase(db: databaseInfo) {
        eventsCollector.default.reportEvent("databases", "compact");
        
        this.confirmationMessage("Are you sure?", `Do you want to compact '${generalUtils.escapeHtml(db.name)}'?`, {
            buttons: ["No", "Yes, compact"],
            html: true
        })
            .done(result => {
                if (result.can) {
                    
                    new getIndexNamesCommand(databasesManager.default.getDatabaseByName(db.name))
                        .execute()
                        .done(indexNames => {
                            db.inProgressAction("Compacting...");

                            new compactDatabaseCommand(db.name, true, indexNames)
                                .execute()
                                .done(result => {

                                    notificationCenter.instance.monitorOperation(null, result.OperationId)
                                        .always(() => db.inProgressAction(null));

                                    notificationCenter.instance.openDetailsForOperationById(null, result.OperationId);
                                })
                                .fail(() => db.inProgressAction(null));
                        });
                }
            });
    }

    togglePauseDatabaseIndexing(db: databaseInfo) {
        eventsCollector.default.reportEvent("databases", "pause-indexing");
        
        const pauseIndexing = db.indexingPaused();
        const message = pauseIndexing ? "Resume" : "Pause";

        this.confirmationMessage("Are you sure?", message + " indexing?")
            .done(result => {
                if (result.can) {
                    db.inProgressAction(pauseIndexing ? "Resuming..." : "Pausing...");

                    new togglePauseIndexingCommand(pauseIndexing, db.asDatabase())
                        .execute()
                        .done(() => db.indexingPaused(!pauseIndexing))
                        .always(() => db.inProgressAction(null));
                }
            });
    }

    newDatabase() {
        const createDbView = new createDatabase("newDatabase");
        app.showBootstrapDialog(createDbView);
    }
    
    newDatabaseFromBackup() {
        eventsCollector.default.reportEvent("databases", "new-from-backup");
        
        const createDbView = new createDatabase("restore");
        app.showBootstrapDialog(createDbView);
    }
    
    newDatabaseFromLegacyDatafiles() {
        eventsCollector.default.reportEvent("databases", "new-from-legacy");
        
        const createDbView = new createDatabase("legacyMigration");
        app.showBootstrapDialog(createDbView);
    }

    databasePanelClicked(dbInfo: databaseInfo, event: JQueryEventObject) {
        if (generalUtils.canConsumeDelegatedEvent(event)) {
            this.activateDatabase(dbInfo);
            return false;
        }
        
        return true;
    }
    
    activateDatabase(dbInfo: databaseInfo) {
        const db = this.databasesManager.getDatabaseByName(dbInfo.name);
        if (!db || db.disabled() || !db.relevant())
            return true;

        this.databasesManager.activate(db);

        this.updateDatabaseInfo(db.name);
        
        return true; // don't prevent default action as we have links inside links
    }

    createIsLocalDatabaseObservable(dbName: string) {
        return ko.pureComputed(() => {
            const nodeTag = this.clusterManager.localNodeTag();
            return this.databases().getByName(dbName).isLocal(nodeTag);
        });
    }
}

export = databases;
