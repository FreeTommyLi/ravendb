import commandBase = require("commands/commandBase");
import database = require("models/database");

class extendRaftClusterCommand extends commandBase {


    constructor(private db: database, private connectionInfo: nodeConnectionInfoDto, private create: boolean) {
        super();
    }

    execute(): JQueryPromise<any> {
        if (this.create) {

            return this.post("/admin/cluster/create", ko.toJSON(this.connectionInfo), this.db, { dataType: undefined })
                .done(() => this.reportSuccess("Cluster was created"))
                .fail((response: JQueryXHR) => this.reportError("Failed to create cluster", response.responseText, response.statusText));
        } else {
            return this.post("/admin/cluster/join", ko.toJSON(this.connectionInfo), this.db, { dataType: undefined })
                .done(() => this.reportSuccess("Server was added to cluster."))
                .fail((response: JQueryXHR) => this.reportError("Unable to add server to cluster", response.responseText, response.statusText));
        }
    }
}

export = extendRaftClusterCommand;