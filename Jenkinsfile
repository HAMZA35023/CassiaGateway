pipeline {
    agent {
        docker {
            image 'mcr.microsoft.com/dotnet/sdk:8.0'
            args '-u root:root'
        }
    }

    environment {
        DEV_ENV_NAME      = 'DEV'
        STAGING_ENV_NAME  = 'STAGING'
        PROD_ENV_NAME     = 'PRODUCTION'
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Restore') {
            steps {
                sh "dotnet restore AccessAPP.sln"
            }
        }

        stage('Build') {
            steps {
                sh "dotnet build AccessAPP.sln --configuration Release --no-restore -p:UseAppHost=false"
            }
        }

        stage('Publish') {
            steps {
                sh "dotnet publish AccessAPP/AccessAPP.csproj --configuration Release --output publish --no-build -p:UseAppHost=false"
            }
        }

        stage('Deploy to DEV') {
            steps {
                echo "Deploying build #${env.BUILD_NUMBER} to ${DEV_ENV_NAME}..."
                echo "This is where your Azure DEV deployment command will go later."
               
            }
        }

        stage('Deploy to STAGING') {
            steps {
                script {
                    input message: "Deploy build #${env.BUILD_NUMBER} to ${STAGING_ENV_NAME}?", ok: "Deploy to STAGING"
                }
                echo "Deploying to ${STAGING_ENV_NAME}..."
                echo "This is where your Azure STAGING deployment command will go."
            }
        }

        stage('Deploy to PROD') {
            when {
                anyOf {
                    branch 'main'
                    branch 'master'
                }
            }
            steps {
                script {
                    input message: "Are you sure you want to deploy build #${env.BUILD_NUMBER} to ${PROD_ENV_NAME}?", 
                          ok: "Deploy to PROD"
                }
                echo "Deploying to ${PROD_ENV_NAME}..."
                echo "This is where your Azure PROD deployment command will go."
            }
        }

        
    }
}
